use serde::{Deserialize, Serialize};
use std::path::PathBuf;

const MAX_FILES: usize = 80;
const MAX_FILE_BYTES: usize = 16_384;
const MAX_TOTAL_BYTES: usize = 24_000;
const MAX_CHANGES: usize = 8;
const MAX_PROPOSAL_BYTES: usize = 131_072;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalFolderSelection {
    pub grant_id: String,
    pub name: String,
    pub files: Vec<ExternalWorkspaceFile>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalWorkspaceFile {
    pub path: String,
    pub content: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalFileChange {
    pub operation: String,
    pub path: String,
    pub before: Option<String>,
    pub after: Option<String>,
}

#[derive(Default)]
pub struct ExternalWorkspaceManager {
    #[cfg(windows)]
    inner: windows_impl::Inner,
    #[cfg(not(windows))]
    backup_root: PathBuf,
}

impl ExternalWorkspaceManager {
    pub fn new(backup_root: PathBuf) -> Result<Self, String> {
        #[cfg(windows)]
        {
            Ok(Self { inner: windows_impl::Inner::new(backup_root)? })
        }
        #[cfg(not(windows))]
        {
            Ok(Self { backup_root })
        }
    }

    pub fn select_folder(&self, path: PathBuf) -> Result<ExternalFolderSelection, String> {
        #[cfg(windows)]
        { self.inner.select_folder(path) }
        #[cfg(not(windows))]
        {
            let _ = path;
            Err("Native external-folder access is supported only in AIPlatform Studio Desktop for Windows.".into())
        }
    }

    pub fn enable_writes(&self, grant_id: &str) -> Result<(), String> {
        #[cfg(windows)]
        { self.inner.enable_writes(grant_id) }
        #[cfg(not(windows))]
        {
            let _ = grant_id;
            Err("Native external-folder writing is supported only in AIPlatform Studio Desktop for Windows.".into())
        }
    }

    pub fn release(&self, grant_id: &str) {
        #[cfg(windows)]
        self.inner.release(grant_id);
        #[cfg(not(windows))]
        let _ = grant_id;
    }

    pub fn apply(
        &self, grant_id: &str, proposal_id: &str, changes: Vec<ExternalFileChange>,
    ) -> Result<String, String> {
        #[cfg(windows)]
        { self.inner.apply(grant_id, proposal_id, changes) }
        #[cfg(not(windows))]
        {
            let _ = (grant_id, proposal_id, changes);
            Err("Native external-folder writing is supported only in AIPlatform Studio Desktop for Windows.".into())
        }
    }

    pub fn restore(&self, grant_id: &str, backup_id: &str) -> Result<(), String> {
        #[cfg(windows)]
        { self.inner.restore(grant_id, backup_id) }
        #[cfg(not(windows))]
        {
            let _ = (grant_id, backup_id);
            Err("Native external-folder restoration is supported only in AIPlatform Studio Desktop for Windows.".into())
        }
    }
}

#[cfg(windows)]
mod windows_impl {
    use super::*;
    use serde::{Deserialize, Serialize};
    use std::{
        collections::{HashMap, HashSet},
        ffi::OsStr,
        fs::{self, File, OpenOptions},
        io::{Read, Seek, SeekFrom, Write},
        os::windows::{ffi::OsStrExt, fs::MetadataExt, io::{AsRawHandle, FromRawHandle, RawHandle}},
        path::{Component, Path},
        ptr::{null, null_mut},
        sync::Mutex,
    };
    use windows_sys::Win32::{
        Foundation::{GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE},
        Storage::FileSystem::{
            CreateFileW, GetFileInformationByHandle, GetFinalPathNameByHandleW,
            SetFileInformationByHandle,
            BY_HANDLE_FILE_INFORMATION, FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT,
            FILE_DISPOSITION_INFO, FileDispositionInfo,
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_NAME_NORMALIZED,
            CREATE_NEW, DELETE, FILE_READ_ATTRIBUTES, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
            VOLUME_NAME_DOS,
        },
    };

    const MAX_DEPTH: usize = 24;
    const MAX_VISITED_ENTRIES: usize = 10_000;
    const FILE_FLAGS: u32 = FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS;

    #[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
    struct FileIdentity {
        volume: u32,
        index: u64,
    }

    #[derive(Clone)]
    struct SnapshotEntry {
        content: String,
        identity: FileIdentity,
    }

    struct Grant {
        root: PathBuf,
        root_final: String,
        root_identity: FileIdentity,
        root_handle: File,
        _ancestor_guards: Vec<File>,
        name: String,
        snapshots: HashMap<String, SnapshotEntry>,
        write_enabled: bool,
    }

    #[derive(Deserialize, Serialize)]
    #[serde(rename_all = "camelCase")]
    struct BackupRecord {
        id: String,
        proposal_id: String,
        root_identity: FileIdentity,
        files: Vec<BackupFile>,
    }

    #[derive(Deserialize, Serialize)]
    #[serde(rename_all = "camelCase")]
    struct BackupFile {
        operation: String,
        path: String,
        before: Option<String>,
        after: Option<String>,
        identity: Option<FileIdentity>,
    }

    struct PreparedFile {
        operation: String,
        path: String,
        before: Option<String>,
        after: Option<String>,
        identity: Option<FileIdentity>,
        file: Option<File>,
        _directory_guards: Vec<File>,
        applied: bool,
    }

    pub struct Inner {
        grants: Mutex<HashMap<String, Grant>>,
        backup_root: PathBuf,
    }

    impl Default for Inner {
        fn default() -> Self {
            Self { grants: Mutex::new(HashMap::new()), backup_root: PathBuf::new() }
        }
    }

    impl Inner {
        pub fn new(backup_root: PathBuf) -> Result<Self, String> {
            fs::create_dir_all(&backup_root)
                .map_err(|error| format!("Cannot prepare the private backup directory: {error}"))?;
            reject_reparse_components(&backup_root)?;
            Ok(Self { grants: Mutex::new(HashMap::new()), backup_root })
        }

        pub fn select_folder(&self, path: PathBuf) -> Result<ExternalFolderSelection, String> {
            let root = open_selected_root(path)?;
            let name = root.root.file_name().and_then(OsStr::to_str)
                .filter(|value| !value.is_empty())
                .ok_or("The selected folder has no usable display name.")?
                .to_owned();
            let mut grant = Grant {
                root: root.root,
                root_final: root.root_final,
                root_identity: root.root_identity,
                root_handle: root.root_handle,
                _ancestor_guards: root.ancestor_guards,
                name,
                snapshots: HashMap::new(),
                write_enabled: false,
            };
            let files = scan_workspace(&grant)?;
            grant.snapshots = files.iter().map(|file| {
                let identity = file_identity_for_path(&grant, &file.path)?;
                Ok((file.path.clone(), SnapshotEntry { content: file.content.clone(), identity }))
            }).collect::<Result<HashMap<_, _>, String>>()?;

            let grant_id = random_id()?;
            let selection = ExternalFolderSelection {
                grant_id: grant_id.clone(),
                name: grant.name.clone(),
                files,
            };
            self.grants.lock().map_err(|_| "External-folder authorization state is unavailable.")?
                .insert(grant_id, grant);
            Ok(selection)
        }

        pub fn enable_writes(&self, grant_id: &str) -> Result<(), String> {
            let mut grants = self.grants.lock().map_err(|_| "External-folder authorization state is unavailable.")?;
            let grant = grants.get_mut(grant_id).ok_or("Reselect the external folder to authorize it.")?;
            verify_root(grant)?;
            grant.write_enabled = true;
            Ok(())
        }

        pub fn release(&self, grant_id: &str) {
            if let Ok(mut grants) = self.grants.lock() {
                grants.remove(grant_id);
            }
        }

        pub fn apply(
            &self, grant_id: &str, proposal_id: &str, changes: Vec<ExternalFileChange>,
        ) -> Result<String, String> {
            validate_guid(proposal_id, "proposal")?;
            if changes.is_empty() || changes.len() > MAX_CHANGES {
                return Err(format!("A proposal must contain between 1 and {MAX_CHANGES} file changes."));
            }
            let mut grants = self.grants.lock().map_err(|_| "External-folder authorization state is unavailable.")?;
            let grant = grants.get_mut(grant_id).ok_or("Reselect the external folder before applying changes.")?;
            if !grant.write_enabled {
                return Err("Enable writing for this selected folder before applying a proposal.".into());
            }
            verify_root(grant)?;

            let mut seen = HashSet::new();
            let mut total_bytes = 0usize;
            let mut prepared = Vec::with_capacity(changes.len());
            for change in changes {
                validate_source_path(&change.path)?;
                if !seen.insert(change.path.to_ascii_lowercase()) {
                    return Err("The proposal contains duplicate file paths.".into());
                }
                let expected = match change.operation.as_str() {
                    "create" if change.before.is_none() && change.after.is_some() => false,
                    "modify" if change.before.is_some() && change.after.is_some() => true,
                    "delete" if change.before.is_some() && change.after.is_none() => true,
                    _ => return Err("The proposal contains an invalid create/modify/delete operation.".into()),
                };
                let before_bytes = change.before.as_ref().map_or(0, String::len);
                let after_bytes = change.after.as_ref().map_or(0, String::len);
                if before_bytes > MAX_FILE_BYTES ||
                    change.after.as_ref().is_some_and(|after|
                        after.is_empty() || after.len() > MAX_FILE_BYTES || after.contains('\0')) ||
                    change.after.is_some() && change.after == change.before {
                    return Err("The proposal contains an empty, unchanged, binary, or oversized file.".into());
                }
                total_bytes = total_bytes.saturating_add(before_bytes).saturating_add(after_bytes);
                if total_bytes > MAX_PROPOSAL_BYTES {
                    return Err("The proposed changes exceed the workspace size limit.".into());
                }
                let mut file = None;
                let mut identity = None;
                let mut guards = Vec::new();
                if expected {
                    let snapshot = grant.snapshots.get(&change.path)
                        .ok_or("The proposal refers to a file that was not granted by the native folder selection.")?;
                    if Some(snapshot.content.as_str()) != change.before.as_deref() {
                        return Err(format!("'{}' differs from the authorized snapshot. Select the folder again and request a new proposal.", change.path));
                    }
                    let (opened, held, file_identity, current) = open_target(grant, &change.path, true)?;
                    if file_identity != snapshot.identity || Some(current.as_str()) != change.before.as_deref() {
                        return Err(format!("'{}' changed since the proposal was created. No files were modified.", change.path));
                    }
                    file = Some(opened);
                    identity = Some(file_identity);
                    guards = held;
                } else {
                    if grant.snapshots.contains_key(&change.path) {
                        return Err(format!("'{}' already exists in the authorized snapshot.", change.path));
                    }
                    let target = join_relative(&grant.root, &change.path);
                    match fs::symlink_metadata(&target) {
                        Ok(_) => return Err(format!("'{}' now exists. Select the folder again before creating it.", change.path)),
                        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                        Err(error) => return Err(format!("Cannot verify new path '{}': {error}", change.path)),
                    }
                }
                prepared.push(PreparedFile {
                    operation: change.operation,
                    path: change.path,
                    before: change.before,
                    after: change.after,
                    identity,
                    file,
                    _directory_guards: std::mem::take(&mut guards),
                    applied: false,
                });
            }

            let backup_id = random_id()?;
            let record = BackupRecord {
                id: backup_id.clone(),
                proposal_id: proposal_id.to_owned(),
                root_identity: grant.root_identity,
                files: prepared.iter().map(|file| BackupFile {
                    operation: file.operation.clone(),
                    path: file.path.clone(),
                    before: file.before.clone(),
                    after: file.after.clone(),
                    identity: file.identity,
                }).collect(),
            };
            let backup_path = self.store_backup(&record)?;
            let mut created_directories = Vec::new();
            let apply_result = apply_prepared(grant, &mut prepared, &mut created_directories);
            if let Err(error) = apply_result {
                if let Err(rollback) = rollback_prepared(&mut prepared) {
                    return Err(format!(
                        "The change failed and rollback could not be completed. Keep backup {backup_id} for recovery. Failure: {error}; rollback: {rollback}"
                    ));
                }
                for item in &mut prepared { item.file.take(); }
                remove_empty_directories(&created_directories);
                let _ = fs::remove_file(backup_path);
                return Err(format!("The change was rolled back: {error}"));
            }

            for item in &prepared {
                if item.operation == "delete" {
                    grant.snapshots.remove(&item.path);
                } else {
                    grant.snapshots.insert(item.path.clone(), SnapshotEntry {
                        content: item.after.clone().ok_or("Applied file content is missing.")?,
                        identity: item.identity.ok_or("Applied file identity is missing.")?,
                    });
                }
            }
            Ok(backup_id)
        }

        pub fn restore(&self, grant_id: &str, backup_id: &str) -> Result<(), String> {
            validate_backup_id(backup_id)?;
            let mut grants = self.grants.lock().map_err(|_| "External-folder authorization state is unavailable.")?;
            let grant = grants.get_mut(grant_id).ok_or("Reselect the folder associated with this backup.")?;
            if !grant.write_enabled {
                return Err("Enable writing for this selected folder before restoring files.".into());
            }
            verify_root(grant)?;
            let marker = self.backup_marker(backup_id);
            if marker.exists() {
                return Err("This backup has already been restored.".into());
            }
            let record = self.load_backup(backup_id)?;
            if record.id != backup_id || record.root_identity != grant.root_identity {
                return Err("This backup belongs to a different selected folder.".into());
            }

            let mut prepared = Vec::with_capacity(record.files.len());
            for saved in &record.files {
                validate_source_path(&saved.path)?;
                match saved.operation.as_str() {
                    "create" => {
                        let expected = saved.after.as_deref().ok_or("The recovery backup is incomplete.")?;
                        let snapshot = grant.snapshots.get(&saved.path)
                            .ok_or("Reselect the folder so created files can be verified before restoration.")?;
                        let (file, guards, identity, current) = open_target(grant, &saved.path, true)?;
                        if snapshot.content != expected || snapshot.identity != identity ||
                            current != expected || saved.identity.is_some_and(|saved_id| saved_id != identity) {
                            return Err(format!("'{}' changed after application. Restoration was refused.", saved.path));
                        }
                        prepared.push(PreparedFile {
                            operation: "delete".into(), path: saved.path.clone(),
                            before: Some(expected.into()), after: None, identity: Some(identity),
                            file: Some(file), _directory_guards: guards, applied: false,
                        });
                    }
                    "modify" => {
                        let expected = saved.after.as_deref().ok_or("The recovery backup is incomplete.")?;
                        let replacement = saved.before.as_deref().ok_or("The recovery backup is incomplete.")?;
                        let snapshot = grant.snapshots.get(&saved.path)
                            .ok_or("Reselect the folder so modified files can be verified before restoration.")?;
                        let (file, guards, identity, current) = open_target(grant, &saved.path, true)?;
                        if snapshot.content != expected || Some(snapshot.identity) != saved.identity ||
                            current != expected || identity != snapshot.identity {
                            return Err(format!("'{}' changed after application. Restoration was refused.", saved.path));
                        }
                        prepared.push(PreparedFile {
                            operation: "modify".into(), path: saved.path.clone(),
                            before: Some(expected.into()), after: Some(replacement.into()), identity: Some(identity),
                            file: Some(file), _directory_guards: guards, applied: false,
                        });
                    }
                    "delete" => {
                        if grant.snapshots.contains_key(&saved.path) {
                            return Err(format!("'{}' exists again. Restoration was refused.", saved.path));
                        }
                        let original = saved.before.as_deref().ok_or("The recovery backup is incomplete.")?;
                        prepared.push(PreparedFile {
                            operation: "create".into(), path: saved.path.clone(),
                            before: None, after: Some(original.into()), identity: None,
                            file: None, _directory_guards: Vec::new(), applied: false,
                        });
                    }
                    _ => return Err("The recovery backup contains an unsupported file operation.".into()),
                }
            }

            let mut created_directories = Vec::new();
            if let Err(error) = apply_prepared(grant, &mut prepared, &mut created_directories) {
                if let Err(rollback) = rollback_prepared(&mut prepared) {
                    return Err(format!("Restoration failed and the applied version could not be recovered: {error}; {rollback}"));
                }
                for item in &mut prepared { item.file.take(); }
                remove_empty_directories(&created_directories);
                return Err(format!("Restoration was rolled back: {error}"));
            }
            if let Err(error) = self.mark_restored(backup_id) {
                if let Err(rollback) = rollback_prepared(&mut prepared) {
                    return Err(format!("Files were restored but restore-status rollback failed: {error}; {rollback}"));
                }
                for item in &mut prepared { item.file.take(); }
                remove_empty_directories(&created_directories);
                return Err(error);
            }
            for item in &prepared {
                if item.operation == "delete" {
                    grant.snapshots.remove(&item.path);
                } else {
                    grant.snapshots.insert(item.path.clone(), SnapshotEntry {
                        content: item.after.clone().ok_or("Restored file content is missing.")?,
                        identity: item.identity.ok_or("Restored file identity is missing.")?,
                    });
                }
            }
            Ok(())
        }

        fn store_backup(&self, record: &BackupRecord) -> Result<PathBuf, String> {
            let path = self.backup_path(&record.id);
            let temporary = self.backup_root.join(format!("{}.tmp", record.id));
            let content = serde_json::to_vec(record).map_err(|error| format!("Cannot encode file backup: {error}"))?;
            let mut file = OpenOptions::new().write(true).create_new(true).open(&temporary)
                .map_err(|error| format!("Cannot create a private recovery backup: {error}"))?;
            file.write_all(&content).and_then(|_| file.sync_all())
                .map_err(|error| format!("Cannot persist the recovery backup: {error}"))?;
            fs::rename(&temporary, &path).map_err(|error| format!("Cannot finalize the recovery backup: {error}"))?;
            Ok(path)
        }

        fn load_backup(&self, backup_id: &str) -> Result<BackupRecord, String> {
            let path = self.backup_path(backup_id);
            reject_reparse_components(&path)?;
            let metadata = fs::symlink_metadata(&path)
                .map_err(|_| "The recovery backup is missing or unavailable.")?;
            if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
                metadata.len() > (MAX_TOTAL_BYTES * 4) as u64 {
                return Err("The recovery backup cannot be verified.".into());
            }
            let record: BackupRecord = serde_json::from_slice(
                &fs::read(path).map_err(|error| format!("Cannot read recovery backup: {error}"))?)
                .map_err(|error| format!("Invalid recovery backup: {error}"))?;
            if record.files.is_empty() || record.files.len() > MAX_CHANGES {
                return Err("The recovery backup has an invalid file list.".into());
            }
            Ok(record)
        }

        fn mark_restored(&self, backup_id: &str) -> Result<(), String> {
            let marker = self.backup_marker(backup_id);
            OpenOptions::new().write(true).create_new(true).open(marker)
                .and_then(|mut file| file.write_all(b"restored"))
                .map_err(|error| format!("Files were restored, but the restore status could not be saved: {error}"))
        }

        fn backup_path(&self, backup_id: &str) -> PathBuf {
            self.backup_root.join(format!("{backup_id}.json"))
        }

        fn backup_marker(&self, backup_id: &str) -> PathBuf {
            self.backup_root.join(format!("{backup_id}.restored"))
        }
    }

    struct OpenRoot {
        root: PathBuf,
        root_final: String,
        root_identity: FileIdentity,
        root_handle: File,
        ancestor_guards: Vec<File>,
    }

    fn open_selected_root(path: PathBuf) -> Result<OpenRoot, String> {
        if !path.is_absolute() {
            return Err("The native folder picker returned a non-absolute path.".into());
        }
        reject_reparse_components(&path)?;
        let mut current = PathBuf::new();
        let mut parent_final: Option<String> = None;
        let mut ancestor_guards = Vec::new();
        let mut root_handle = None;
        for component in path.components() {
            match component {
                Component::Prefix(_) | Component::RootDir | Component::Normal(_) => {
                    current.push(component.as_os_str());
                    if !current.is_absolute() {
                        continue;
                    }
                    let directory = open_directory(&current, FILE_SHARE_READ | FILE_SHARE_WRITE)?;
                    let info = handle_info(&directory)?;
                    if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
                        info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0 {
                        return Err("The selected path contains a symbolic link, junction, or non-directory.".into());
                    }
                    let resolved = final_path(&directory)?;
                    if parent_final.as_ref().map(|parent| !is_within(parent, &resolved)).unwrap_or(false) {
                        return Err("A selected path component resolves outside its verified parent.".into());
                    }
                    if let Some(previous) = root_handle.replace(directory) {
                        ancestor_guards.push(previous);
                    }
                    parent_final = Some(resolved);
                }
                Component::CurDir | Component::ParentDir =>
                    return Err("The selected path contains a non-canonical component.".into()),
            }
        }
        let root_handle = root_handle.ok_or("The native folder picker returned an invalid folder path.")?;
        let root = fs::canonicalize(&path)
            .map_err(|error| format!("Cannot resolve the selected folder: {error}"))?;
        let info = handle_info(&root_handle)?;
        if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
            info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0 {
            return Err("The selected folder is a reparse point or is not a directory.".into());
        }
        let root_final = final_path(&root_handle)?;
        if !same_path(&root_final, &root.to_string_lossy()) {
            return Err("The selected folder changed while it was being authorized.".into());
        }
        Ok(OpenRoot {
            root,
            root_final,
            root_identity: identity(&info),
            root_handle,
            ancestor_guards,
        })
    }

    fn scan_workspace(grant: &Grant) -> Result<Vec<ExternalWorkspaceFile>, String> {
        verify_root(grant)?;
        let mut files = Vec::new();
        let mut visited = 0usize;
        let mut total = 0usize;
        walk_directory(grant, "", 0, &mut visited, &mut total, &mut files)?;
        if files.is_empty() {
            return Err("No eligible text source files were found in the selected folder.".into());
        }
        Ok(files)
    }

    fn walk_directory(
        grant: &Grant, relative: &str, depth: usize, visited: &mut usize,
        total: &mut usize, files: &mut Vec<ExternalWorkspaceFile>,
    ) -> Result<(), String> {
        if depth > MAX_DEPTH {
            return Err("The selected folder is nested too deeply to verify safely.".into());
        }
        let _guards = lock_directory_chain(grant, relative)?;
        let directory = join_relative(&grant.root, relative);
        let mut entries = fs::read_dir(&directory)
            .map_err(|error| format!("Cannot inspect the selected folder: {error}"))?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|error| format!("Cannot inspect the selected folder: {error}"))?;
        entries.sort_by_key(|entry| entry.file_name());

        for entry in entries {
            *visited += 1;
            if *visited > MAX_VISITED_ENTRIES {
                return Err("The selected folder contains too many entries to verify safely.".into());
            }
            let name = entry.file_name().into_string()
                .map_err(|_| "A filename is not valid Unicode and cannot be safely authorized.")?;
            let child = if relative.is_empty() { name.clone() } else { format!("{relative}/{name}") };
            validate_relative_path(&child)?;
            let metadata = fs::symlink_metadata(entry.path())
                .map_err(|error| format!("Cannot inspect '{child}': {error}"))?;
            if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
                return Err(format!("'{child}' is a symbolic link, junction, or other reparse point. The folder was not authorized."));
            }

            if metadata.is_dir() {
                if !is_excluded_directory(&name) {
                    walk_directory(grant, &child, depth + 1, visited, total, files)?;
                }
                continue;
            }
            if !is_source_path(&child) || files.len() >= MAX_FILES || *total >= MAX_TOTAL_BYTES {
                continue;
            }
            let (_, _guards, _file_identity, content) = open_target(grant, &child, false)?;
            let size = content.len();
            if total.saturating_add(size) > MAX_TOTAL_BYTES {
                continue;
            }
            *total += size;
            files.push(ExternalWorkspaceFile { path: child, content });
        }
        Ok(())
    }

    fn file_identity_for_path(grant: &Grant, relative: &str) -> Result<FileIdentity, String> {
        let (_, _, identity, _) = open_target(grant, relative, false)?;
        Ok(identity)
    }

    fn open_target(
        grant: &Grant, relative: &str, write: bool,
    ) -> Result<(File, Vec<File>, FileIdentity, String), String> {
        validate_source_path(relative)?;
        let parent = Path::new(relative).parent()
            .filter(|path| !path.as_os_str().is_empty())
            .map(|path| path.to_string_lossy().replace('\\', "/"))
            .unwrap_or_default();
        let guards = lock_directory_chain(grant, &parent)?;
        let path = join_relative(&grant.root, relative);
        let access = if write { GENERIC_READ | GENERIC_WRITE | DELETE } else { GENERIC_READ };
        let file = open_handle(&path, access, FILE_SHARE_READ, FILE_FLAG_OPEN_REPARSE_POINT)
            .map_err(|error| format!("Cannot safely open '{relative}': {error}"))?;
        let info = handle_info(&file)?;
        validate_file_info(&info, false)?;
        if !is_within(&grant.root_final, &final_path(&file)?) {
            return Err(format!("'{relative}' resolves outside the authorized folder."));
        }
        if info.nNumberOfLinks != 1 {
            return Err(format!("'{relative}' has multiple hard links and cannot be safely modified."));
        }
        let identity = identity(&info);
        let mut reader = file.try_clone().map_err(|error| format!("Cannot verify '{relative}': {error}"))?;
        let content = read_text(&mut reader, relative)?;
        Ok((file, guards, identity, content))
    }

    fn lock_directory_chain(grant: &Grant, relative: &str) -> Result<Vec<File>, String> {
        verify_root(grant)?;
        let mut guards = Vec::new();
        let root = open_directory(&grant.root, FILE_SHARE_READ)?;
        validate_directory(&root, &grant.root_final, true)?;
        guards.push(root);
        if relative.is_empty() {
            return Ok(guards);
        }
        let mut current = grant.root.clone();
        for segment in relative.split('/') {
            if segment.is_empty() || segment == "." || segment == ".." {
                return Err("The selected path contains an invalid folder segment.".into());
            }
            current.push(segment);
            let directory = open_directory(&current, FILE_SHARE_READ)
                .map_err(|error| format!("Cannot lock a verified folder: {error}"))?;
            validate_directory(&directory, &grant.root_final, false)?;
            guards.push(directory);
        }
        Ok(guards)
    }

    fn verify_root(grant: &Grant) -> Result<(), String> {
        let info = handle_info(&grant.root_handle)?;
        if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
            info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0 ||
            identity(&info) != grant.root_identity ||
            !same_path(&final_path(&grant.root_handle)?, &grant.root_final) {
            return Err("The authorized folder changed or can no longer be verified. Select it again.".into());
        }
        Ok(())
    }

    fn validate_directory(file: &File, root: &str, is_root: bool) -> Result<(), String> {
        let info = handle_info(file)?;
        if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
            info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY == 0 {
            return Err("A path component is a symbolic link, junction, or non-directory.".into());
        }
        let path = final_path(file)?;
        if is_root {
            if !same_path(&path, root) {
                return Err("The authorized folder handle no longer matches its grant.".into());
            }
        } else if !is_within(root, &path) {
            return Err("A resolved folder is outside the authorized directory.".into());
        }
        Ok(())
    }

    fn validate_file_info(info: &BY_HANDLE_FILE_INFORMATION, expect_directory: bool) -> Result<(), String> {
        if info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
            (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0) != expect_directory {
            return Err("The file is a reparse point or has an unexpected type.".into());
        }
        Ok(())
    }

    fn read_text(file: &mut File, relative: &str) -> Result<String, String> {
        let size = file.metadata().map_err(|error| format!("Cannot inspect '{relative}': {error}"))?.len();
        if size > MAX_FILE_BYTES as u64 {
            return Err(format!("'{relative}' exceeds the authorized file size limit."));
        }
        file.seek(SeekFrom::Start(0)).map_err(|error| format!("Cannot read '{relative}': {error}"))?;
        let mut bytes = Vec::with_capacity(size as usize);
        file.take((MAX_FILE_BYTES + 1) as u64).read_to_end(&mut bytes)
            .map_err(|error| format!("Cannot read '{relative}': {error}"))?;
        if bytes.len() > MAX_FILE_BYTES || bytes.contains(&0) {
            return Err(format!("'{relative}' is binary or exceeds the authorized file size limit."));
        }
        String::from_utf8(bytes).map_err(|_| format!("'{relative}' is not valid UTF-8 text."))
    }

    fn apply_prepared(
        grant: &Grant, files: &mut [PreparedFile], created_directories: &mut Vec<PathBuf>,
    ) -> Result<(), String> {
        for item in files {
            match item.operation.as_str() {
                "create" => {
                    let (file, guards, identity) =
                        create_new_target(grant, &item.path, created_directories)?;
                    item.file = Some(file);
                    item.identity = Some(identity);
                    item._directory_guards = guards;
                    item.applied = true;
                    let content = item.after.as_deref().ok_or("New file content is missing.")?;
                    write_file_content(item.file.as_mut().unwrap(), &item.path, content)?;
                }
                "modify" => {
                    item.applied = true;
                    let content = item.after.as_deref().ok_or("Replacement content is missing.")?;
                    write_file_content(item.file.as_mut().ok_or("The verified file handle is missing.")?,
                        &item.path, content)?;
                }
                "delete" => {
                    set_delete_disposition(item.file.as_ref().ok_or("The verified file handle is missing.")?, true)?;
                    item.applied = true;
                }
                _ => return Err("The proposal contains an unsupported file operation.".into()),
            }
        }
        Ok(())
    }

    fn rollback_prepared(files: &mut [PreparedFile]) -> Result<(), String> {
        let mut failures = Vec::new();
        for item in files.iter_mut().rev().filter(|item| item.applied) {
            let result = match item.operation.as_str() {
                "create" => set_delete_disposition(
                    item.file.as_ref().ok_or("The created file handle is missing.")?, true),
                "modify" => write_file_content(
                    item.file.as_mut().ok_or("The modified file handle is missing.")?,
                    &item.path, item.before.as_deref().ok_or("The original content is missing.")?),
                "delete" => set_delete_disposition(
                    item.file.as_ref().ok_or("The deleted file handle is missing.")?, false),
                _ => Err("The proposal contains an unsupported rollback operation.".into()),
            };
            match result {
                Ok(()) => item.applied = false,
                Err(error) => failures.push(format!("{}: {error}", item.path)),
            }
        }
        if failures.is_empty() { Ok(()) }
        else { Err(failures.join("; ")) }
    }

    fn write_file_content(file: &mut File, path: &str, content: &str) -> Result<(), String> {
        file.seek(SeekFrom::Start(0)).and_then(|_| file.set_len(0))
            .and_then(|_| file.write_all(content.as_bytes()))
            .and_then(|_| file.sync_all())
            .map_err(|error| format!("Cannot write '{path}': {error}"))?;
        if read_text(file, path)? != content {
            return Err(format!("The verified write for '{path}' did not persist exactly."));
        }
        Ok(())
    }

    fn set_delete_disposition(file: &File, delete: bool) -> Result<(), String> {
        let info = FILE_DISPOSITION_INFO { DeleteFile: delete };
        let result = unsafe {
            SetFileInformationByHandle(
                file.as_raw_handle() as HANDLE,
                FileDispositionInfo,
                &info as *const _ as *const _,
                std::mem::size_of::<FILE_DISPOSITION_INFO>() as u32,
            )
        };
        if result == 0 {
            Err(format!("Cannot {} a verified workspace file: {}",
                if delete { "delete" } else { "restore" }, std::io::Error::last_os_error()))
        } else {
            Ok(())
        }
    }

    fn create_new_target(
        grant: &Grant, relative: &str, created_directories: &mut Vec<PathBuf>,
    ) -> Result<(File, Vec<File>, FileIdentity), String> {
        validate_source_path(relative)?;
        let parent = Path::new(relative).parent()
            .filter(|path| !path.as_os_str().is_empty())
            .map(|path| path.to_string_lossy().replace('\\', "/"))
            .unwrap_or_default();
        let guards = lock_or_create_directory_chain(grant, &parent, created_directories)?;
        let path = join_relative(&grant.root, relative);
        let file = create_handle(&path)
            .map_err(|error| format!("Cannot create '{relative}' without replacing an existing file: {error}"))?;
        let info = handle_info(&file)?;
        validate_file_info(&info, false)?;
        if info.nNumberOfLinks != 1 || !is_within(&grant.root_final, &final_path(&file)?) {
            return Err(format!("The new file '{relative}' cannot be verified inside the authorized folder."));
        }
        Ok((file, guards, identity(&info)))
    }

    fn lock_or_create_directory_chain(
        grant: &Grant, relative: &str, created_directories: &mut Vec<PathBuf>,
    ) -> Result<Vec<File>, String> {
        verify_root(grant)?;
        let mut guards = Vec::new();
        let root = open_directory(&grant.root, FILE_SHARE_READ)?;
        validate_directory(&root, &grant.root_final, true)?;
        guards.push(root);
        if relative.is_empty() { return Ok(guards); }
        let mut current = grant.root.clone();
        for segment in relative.split('/') {
            if segment.is_empty() || segment == "." || segment == ".." {
                return Err("The selected path contains an invalid folder segment.".into());
            }
            current.push(segment);
            match fs::symlink_metadata(&current) {
                Ok(metadata) if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 =>
                    return Err("A path component is a symbolic link or junction.".into()),
                Ok(metadata) if !metadata.is_dir() =>
                    return Err("A parent path component is not a folder.".into()),
                Ok(_) => {}
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                    fs::create_dir(&current)
                        .map_err(|error| format!("Cannot create a workspace subfolder: {error}"))?;
                    created_directories.push(current.clone());
                }
                Err(error) => return Err(format!("Cannot verify a workspace subfolder: {error}")),
            }
            let directory = open_directory(&current, FILE_SHARE_READ)?;
            validate_directory(&directory, &grant.root_final, false)?;
            guards.push(directory);
        }
        Ok(guards)
    }

    fn remove_empty_directories(directories: &[PathBuf]) {
        for directory in directories.iter().rev() {
            let _ = fs::remove_dir(directory);
        }
    }

    fn open_directory(path: &Path, share: u32) -> Result<File, String> {
        open_handle(path, FILE_READ_ATTRIBUTES, share, FILE_FLAGS)
            .map_err(|error| format!("Cannot open the selected folder without following reparse points: {error}"))
    }

    fn open_handle(path: &Path, access: u32, share: u32, flags: u32) -> std::io::Result<File> {
        open_handle_with_disposition(path, access, share, OPEN_EXISTING, flags)
    }

    fn create_handle(path: &Path) -> std::io::Result<File> {
        open_handle_with_disposition(path, GENERIC_READ | GENERIC_WRITE | DELETE,
            FILE_SHARE_READ, CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT)
    }

    fn open_handle_with_disposition(
        path: &Path, access: u32, share: u32, disposition: u32, flags: u32,
    ) -> std::io::Result<File> {
        let wide = path.as_os_str().encode_wide().chain(Some(0)).collect::<Vec<_>>();
        let raw = unsafe {
            CreateFileW(
                wide.as_ptr(),
                access,
                share,
                null(),
                disposition,
                flags,
                null_mut(),
            )
        };
        if raw == INVALID_HANDLE_VALUE {
            return Err(std::io::Error::last_os_error());
        }
        Ok(unsafe { File::from_raw_handle(raw as RawHandle) })
    }

    fn handle_info(file: &File) -> Result<BY_HANDLE_FILE_INFORMATION, String> {
        let mut info = BY_HANDLE_FILE_INFORMATION::default();
        let result = unsafe {
            GetFileInformationByHandle(file.as_raw_handle() as HANDLE, &mut info)
        };
        if result == 0 {
            return Err(format!("Cannot verify a Windows file handle: {}", std::io::Error::last_os_error()));
        }
        Ok(info)
    }

    fn final_path(file: &File) -> Result<String, String> {
        let mut buffer = vec![0u16; 32_768];
        let length = unsafe {
            GetFinalPathNameByHandleW(
                file.as_raw_handle() as HANDLE,
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                FILE_NAME_NORMALIZED | VOLUME_NAME_DOS,
            )
        };
        if length == 0 || length as usize >= buffer.len() {
            return Err(format!("Cannot resolve a verified Windows file handle: {}", std::io::Error::last_os_error()));
        }
        String::from_utf16(&buffer[..length as usize])
            .map_err(|_| "A verified Windows path could not be decoded.".into())
    }

    fn identity(info: &BY_HANDLE_FILE_INFORMATION) -> FileIdentity {
        FileIdentity {
            volume: info.dwVolumeSerialNumber,
            index: ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64,
        }
    }

    fn reject_reparse_components(path: &Path) -> Result<(), String> {
        let mut current = PathBuf::new();
        for component in path.components() {
            match component {
                Component::Prefix(_) | Component::RootDir | Component::Normal(_) => {
                    current.push(component.as_os_str());
                    match fs::symlink_metadata(&current) {
                        Ok(metadata) if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 =>
                            return Err("The selected path contains a symbolic link, junction, or other reparse point.".into()),
                        Ok(_) => {}
                        Err(error) if current == path =>
                            return Err(format!("The selected path cannot be verified: {error}")),
                        Err(_) => {}
                    }
                }
                Component::CurDir | Component::ParentDir =>
                    return Err("The selected path contains a non-canonical component.".into()),
            }
        }
        Ok(())
    }

    fn validate_source_path(path: &str) -> Result<(), String> {
        validate_relative_path(path)?;
        if !is_source_path(path) {
            return Err("Only eligible text source files from the authorized snapshot may be changed.".into());
        }
        Ok(())
    }

    fn validate_relative_path(path: &str) -> Result<(), String> {
        if path.is_empty() || path.len() > 512 || path.starts_with('/') ||
            path.contains('\\') || path.contains(':') || path.contains('\0') {
            return Err("The file path is not a safe relative path.".into());
        }
        for segment in path.split('/') {
            if segment.is_empty() || segment == "." || segment == ".." ||
                segment.ends_with('.') || segment.ends_with(' ') ||
                segment.chars().any(|character| character < ' ' || "<>\"|?*".contains(character)) {
                return Err("The file path contains an unsafe Windows path component.".into());
            }
            let stem = segment.split('.').next().unwrap_or_default().to_ascii_uppercase();
            if ["CON", "PRN", "AUX", "NUL", "CLOCK$"].contains(&stem.as_str()) ||
                ["COM", "LPT"].iter().any(|prefix| {
                    stem.strip_prefix(prefix).is_some_and(|suffix|
                        suffix.len() == 1 && matches!(suffix.as_bytes()[0], b'1'..=b'9'))
                }) {
                return Err("The file path contains a reserved Windows device name.".into());
            }
        }
        Ok(())
    }

    fn is_source_path(path: &str) -> bool {
        let segments = path.split('/').collect::<Vec<_>>();
        if segments.iter().any(|segment| is_excluded_directory(segment)) {
            return false;
        }
        let name = segments.last().copied().unwrap_or_default().to_ascii_lowercase();
        if name.starts_with(".env") || name.starts_with("appsettings") ||
            [".npmrc", ".yarnrc", ".yarnrc.yml", ".pypirc"].contains(&name.as_str()) ||
            ["secret", "credential", "password", "token"].iter().any(|term| name.contains(term)) {
            return false;
        }
        if let Some((_, extension)) = name.rsplit_once('.') {
            if [".pem", ".key", ".p12", ".pfx", ".crt", ".cer"].contains(&format!(".{extension}").as_str()) {
                return false;
            }
            [".cs", ".csproj", ".sln", ".slnx", ".json", ".md", ".ts", ".tsx", ".js", ".jsx",
                ".html", ".css", ".py", ".go", ".rs", ".java", ".xml", ".yaml", ".yml"]
                .contains(&format!(".{extension}").as_str())
        } else {
            name == "dockerfile"
        }
    }

    fn is_excluded_directory(name: &str) -> bool {
        ["bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "coverage", "target",
            ".aws", ".azure", ".ssh", ".docker", ".kube", ".gnupg"]
            .iter().any(|excluded| name.eq_ignore_ascii_case(excluded))
    }

    fn join_relative(root: &Path, relative: &str) -> PathBuf {
        let mut path = root.to_path_buf();
        for segment in relative.split('/').filter(|segment| !segment.is_empty()) {
            path.push(segment);
        }
        path
    }

    fn is_within(root: &str, candidate: &str) -> bool {
        let root = normalized_path(root);
        let candidate = normalized_path(candidate);
        candidate == root || candidate.strip_prefix(&root)
            .map(|suffix| suffix.starts_with('\\'))
            .unwrap_or(false)
    }

    fn same_path(left: &str, right: &str) -> bool {
        normalized_path(left) == normalized_path(right)
    }

    fn normalized_path(path: &str) -> String {
        path.trim_end_matches('\\').to_lowercase()
    }

    fn random_id() -> Result<String, String> {
        let mut bytes = [0u8; 16];
        getrandom::fill(&mut bytes).map_err(|error| format!("Cannot create a folder authorization: {error}"))?;
        Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
    }

    fn validate_guid(value: &str, label: &str) -> Result<(), String> {
        if value.len() != 36 || !value.chars().enumerate().all(|(index, character)| {
            if [8, 13, 18, 23].contains(&index) { character == '-' } else { character.is_ascii_hexdigit() }
        }) {
            return Err(format!("The {label} identifier is invalid."));
        }
        Ok(())
    }

    fn validate_backup_id(value: &str) -> Result<(), String> {
        if value.len() != 32 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("The recovery backup identifier is invalid.".into());
        }
        Ok(())
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use std::{env, time::{SystemTime, UNIX_EPOCH}};
        use windows_sys::Win32::Storage::FileSystem::{
            CreateSymbolicLinkW, SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE,
            SYMBOLIC_LINK_FLAG_DIRECTORY,
        };

        fn temporary_root() -> PathBuf {
            let suffix = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
            env::temp_dir().join(format!("aiplatform-native-workspace-{suffix}"))
        }

        #[test]
        fn applies_and_restores_an_approved_change_in_a_temporary_folder() {
            let root = temporary_root();
            let backups = root.with_extension("backups");
            fs::create_dir_all(root.join("src")).unwrap();
            fs::write(root.join("src/Example.cs"), "class Example { }").unwrap();
            fs::write(root.join("src/Remove.cs"), "class Remove { }").unwrap();
            let manager = ExternalWorkspaceManager::new(backups.clone()).unwrap();
            let selection = manager.select_folder(root.clone()).unwrap();
            manager.enable_writes(&selection.grant_id).unwrap();
            let backup = manager.apply(&selection.grant_id,
                "8dfba8b5-7065-4db4-b6dc-e53df9e16ea4", vec![
            ExternalFileChange {
                operation: "modify".into(),
                path: "src/Example.cs".into(),
                before: Some("class Example { }".into()),
                after: Some("class Example { int Value => 1; }".into()),
            },
            ExternalFileChange {
                operation: "create".into(),
                path: "new/nested/Created.cs".into(),
                before: None,
                after: Some("class Created { }".into()),
            },
            ExternalFileChange {
                operation: "delete".into(),
                path: "src/Remove.cs".into(),
                before: Some("class Remove { }".into()),
                after: None,
            }]).unwrap();
            assert_eq!(fs::read_to_string(root.join("src/Example.cs")).unwrap(),
                "class Example { int Value => 1; }");
            assert_eq!(fs::read_to_string(root.join("new/nested/Created.cs")).unwrap(), "class Created { }");
            assert!(!root.join("src/Remove.cs").exists());
            manager.restore(&selection.grant_id, &backup).unwrap();
            assert_eq!(fs::read_to_string(root.join("src/Example.cs")).unwrap(), "class Example { }");
            assert!(!root.join("new/nested/Created.cs").exists());
            assert_eq!(fs::read_to_string(root.join("src/Remove.cs")).unwrap(), "class Remove { }");
            assert!(manager.restore(&selection.grant_id, &backup).is_err());
            manager.release(&selection.grant_id);
            fs::remove_dir_all(root).unwrap();
            fs::remove_dir_all(backups).unwrap();
        }

        #[test]
        fn refuses_paths_outside_the_snapshot_and_stale_content() {
            let root = temporary_root();
            let backups = root.with_extension("backups");
            fs::create_dir_all(root.join("src")).unwrap();
            fs::write(root.join("src/Example.cs"), "class Example { }").unwrap();
            let manager = ExternalWorkspaceManager::new(backups.clone()).unwrap();
            let selection = manager.select_folder(root.clone()).unwrap();
            manager.enable_writes(&selection.grant_id).unwrap();
            let proposal_id = "8dfba8b5-7065-4db4-b6dc-e53df9e16ea4";
            assert!(manager.apply(&selection.grant_id, proposal_id, vec![ExternalFileChange {
                operation: "modify".into(),
                path: "../outside.cs".into(),
                before: Some("class Example { }".into()),
                after: Some("class Outside { }".into()),
            }]).is_err());
            fs::write(root.join("src/Example.cs"), "class Example { int Changed; }").unwrap();
            assert!(manager.apply(&selection.grant_id, proposal_id, vec![ExternalFileChange {
                operation: "modify".into(),
                path: "src/Example.cs".into(),
                before: Some("class Example { }".into()),
                after: Some("class Example { int Value; }".into()),
            }]).is_err());
            assert_eq!(fs::read_to_string(root.join("src/Example.cs")).unwrap(),
                "class Example { int Changed; }");
            manager.release(&selection.grant_id);
            fs::remove_dir_all(root).unwrap();
            fs::remove_dir_all(backups).unwrap();
        }

        #[test]
        fn rejects_reparse_point_attributes_and_escaping_final_paths() {
            assert!(validate_file_info(&BY_HANDLE_FILE_INFORMATION {
                dwFileAttributes: FILE_ATTRIBUTE_REPARSE_POINT,
                ..Default::default()
            }, false).is_err());
            assert!(!is_within(r"\\?\C:\authorized", r"\\?\C:\authorized-elsewhere\file.cs"));
            assert!(is_within(r"\\?\C:\authorized", r"\\?\C:\authorized\src\file.cs"));
        }

        #[test]
        fn refuses_a_symbolic_link_inside_the_selected_folder() {
            let root = temporary_root();
            let outside = root.with_extension("outside");
            let backups = root.with_extension("backups");
            fs::create_dir_all(&root).unwrap();
            fs::create_dir_all(&outside).unwrap();
            fs::write(outside.join("Example.cs"), "class Example { }").unwrap();
            let link = root.join("linked");
            let link_wide = link.as_os_str().encode_wide().chain(Some(0)).collect::<Vec<_>>();
            let outside_wide = outside.as_os_str().encode_wide().chain(Some(0)).collect::<Vec<_>>();
            let linked = unsafe {
                CreateSymbolicLinkW(
                    link_wide.as_ptr(),
                    outside_wide.as_ptr(),
                    SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE,
                )
            };
            if !linked {
                let error = std::io::Error::last_os_error();
                fs::remove_dir_all(&root).unwrap();
                fs::remove_dir_all(&outside).unwrap();
                if error.raw_os_error() == Some(1314) {
                    eprintln!("Skipping symbolic-link integration check: Windows denied temporary link creation.");
                    return;
                }
                panic!("Could not create a temporary symbolic link for the test: {error}");
            }

            let manager = ExternalWorkspaceManager::new(backups.clone()).unwrap();
            assert!(manager.select_folder(root.clone()).is_err());
            fs::remove_dir_all(root).unwrap();
            fs::remove_dir_all(outside).unwrap();
            fs::remove_dir_all(backups).unwrap();
        }
    }
}

#[cfg(not(windows))]
impl ExternalWorkspaceManager {
    fn unavailable(&self) -> String {
        let _ = &self.backup_root;
        "Native external-folder writing is supported only in AIPlatform Studio Desktop for Windows.".into()
    }
}

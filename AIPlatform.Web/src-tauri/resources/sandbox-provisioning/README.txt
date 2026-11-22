This directory is populated for a Studio release with:

- manifest.json
- aiplatform-sandbox-images-1.0.0.tar

The release build process creates the package with tools/Build-SandboxProvisioning.ps1.
The installed application verifies its SHA-256 and image IDs before allowing import.
These hashes detect accidental changes; they are not a publisher signature.
Do not put credentials, Ollama model files, Docker data directories, or generated projects here.

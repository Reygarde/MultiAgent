FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee AS resolve
USER root
RUN --network=none mkdir -p /seed && chown 10001:10001 /seed
USER 10001:10001
ENV HOME=/tmp NPM_CONFIG_CACHE=/tmp/npm-cache
WORKDIR /seed
COPY --chown=10001:10001 package.json ./
RUN npm install --package-lock-only --ignore-scripts --no-audit --fund=false --legacy-peer-deps

FROM scratch AS lockfile
COPY --from=resolve /seed/package-lock.json /package-lock.json

FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee AS packages
USER root
RUN --network=none mkdir -p /seed && chown 10001:10001 /seed
USER 10001:10001
ENV HOME=/tmp NPM_CONFIG_CACHE=/tmp/npm-cache
WORKDIR /seed
COPY --chown=10001:10001 package.json package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --fund=false --legacy-peer-deps && npm ls --all

FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee
COPY --from=packages /seed/node_modules /opt/aiplatform/node_modules
USER 10001:10001
WORKDIR /work

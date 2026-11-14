ARG BASE_IMAGE=aiplatform-sandbox:net10
ARG REACT_IMAGE=aiplatform-react-sandbox:1.0.0
FROM ${BASE_IMAGE} AS seed
USER 10001:10001
ENV HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY FullStackSandboxPackages.csproj packages.lock.json FullStackNuGet.Config /opt/fullstack-seed/
COPY FullStackRestore.sh /opt/fullstack-restore.sh

FROM seed AS prepare
RUN cp -R /opt/nuget /tmp/fullstack-nuget \
    && sh /opt/fullstack-restore.sh https://api.nuget.org/v3/index.json \
    && mkdir /tmp/fullstack-feed \
    && find /tmp/fullstack-nuget -name '*.nupkg' -exec cp '{}' /tmp/fullstack-feed/ ';' \
    && cd /tmp/fullstack-feed && sha512sum *.nupkg > SHA512SUMS

FROM scratch AS dependency-feed
COPY --from=prepare /tmp/fullstack-feed/ /

FROM seed AS packages
COPY fullstack-feed/ /opt/fullstack-feed/
RUN --network=none cd /opt/fullstack-feed && sha512sum -c SHA512SUMS \
    && sh /opt/fullstack-restore.sh /opt/fullstack-feed

FROM ${REACT_IMAGE} AS frontend

FROM ${BASE_IMAGE}
LABEL org.aiplatform.fullstack.recipe="2"
COPY --from=packages /tmp/fullstack-nuget /opt/nuget
COPY --from=frontend /usr/local/bin/node /usr/local/bin/node
COPY --from=frontend /opt/aiplatform/node_modules /opt/aiplatform/node_modules
ENV HOME=/tmp DOTNET_CLI_HOME=/tmp DOTNET_CLI_TELEMETRY_OPTOUT=1
USER 10001:10001
ENV NPM_CONFIG_CACHE=/tmp/npm-cache
WORKDIR /work

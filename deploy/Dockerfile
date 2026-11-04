# brain-server image. Build from the repository root:
#   docker build -f deploy/Dockerfile -t agent-brain-server .
# The build stage runs natively and cross-targets, so multi-arch builds do not compile under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Set by the release workflow from semantic-release; local builds report 0.0.0-dev.
ARG VERSION=0.0.0-dev
WORKDIR /src
# global.json is not copied: its pinned SDK band need not match the image's SDK.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/AgentBrain.Server -c Release -a $TARGETARCH --self-contained false -p:Version=$VERSION -o /app && mkdir -p /data-template

# Chiseled runtime: non-root, no shell, no package manager.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# A named volume copies this directory's ownership, so the non-root user can write /data.
COPY --from=build --chown=$APP_UID /data-template /data
ENV AGENT_BRAIN_DATA=/data \
    ASPNETCORE_ENVIRONMENT=Production
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD ["/app/brain-server", "health"]
ENTRYPOINT ["/app/brain-server"]
CMD ["run"]

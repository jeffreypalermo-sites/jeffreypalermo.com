# syntax=docker/dockerfile:1

# The site as it ships (ADR-0006): the published app and the content snapshot of one commit (ADR-0002) in one image.
# tests/AcceptanceTests builds and runs this file and replays the URL contract against the container.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Core/JeffreyPalermo.Core.csproj src/Core/
COPY src/Infrastructure/JeffreyPalermo.Infrastructure.csproj src/Infrastructure/
COPY src/UI.Server/JeffreyPalermo.UI.Server.csproj src/UI.Server/
RUN dotnet restore src/UI.Server/JeffreyPalermo.UI.Server.csproj
COPY src/ src/
RUN dotnet publish src/UI.Server/JeffreyPalermo.UI.Server.csproj --configuration Release --no-restore --output /app/publish

# Large uploads are stored with Git LFS. A checkout without LFS holds a small text pointer where each one belongs, and
# the site would serve that pointer. The build stops instead.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS content
COPY content/ /content/
RUN pointers="$(grep --recursive --binary-files=without-match --files-with-matches --line-regexp \
      'version https://git-lfs.github.com/spec/v1' /content/uploads || true)"; \
    if [ -n "$pointers" ]; then \
      echo "These uploads are Git LFS pointers, not files. Run 'git lfs pull' (in CI: check out with lfs: true)."; \
      echo "$pointers"; \
      exit 1; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
# The Build workflow passes the version it also labels the image with; /_health/ready reports it.
ARG VERSION=dev
WORKDIR /app
COPY --from=build /app/publish ./
# Site:ContentPath defaults to "content", relative to the content root /app.
COPY --from=content /content/ ./content/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Site__Version=$VERSION
EXPOSE 8080
# The chiseled image's unprivileged user: no shell, no package manager, and no root.
USER $APP_UID
ENTRYPOINT ["dotnet", "JeffreyPalermo.UI.Server.dll"]

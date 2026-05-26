# Dockerfile — OpenAstroAra.Server daemon for linux-arm64 (RPi 4-5)
# Per design/PORT_PLAYBOOK.md §11.2 + §13 deployment target.
#
# Build context expects ./publish/arm64/ to already contain the self-contained
# .NET publish output. CI's `publish` step produces that via:
#   dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 \
#     --self-contained -p:PublishAot=false -o ./publish/arm64
#
# Base: runtime-deps (NOT aspnet) — because --self-contained bundles the
# .NET + ASP.NET Core runtime DLLs into the publish output, so the base
# image only needs the OS-level runtime dependencies (libc, libssl, ICU
# data, etc). The playbook §11.2 example uses aspnet:10.0-* but that's
# ~120MB of redundant runtime; runtime-deps is the Microsoft-recommended
# base for self-contained deployments and is ~25MB.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm-slim-arm64v8

WORKDIR /app
COPY publish/arm64/ ./

# Default Kestrel port per OpenAstroAra.Server/Program.cs ResolvePort:
# env OPENASTROARA_PORT > appsettings OpenAstroAra:Port > 5555 default.
# (Playbook §11.2 example writes EXPOSE 5400 but the daemon listens on
# 5555 by default — keeping 5555 here so the image's documented port
# matches the daemon's actual listen port without requiring an env-var
# override. Track playbook reconciliation as a separate doc PR.)
EXPOSE 5555

# Non-root per §13 deployment hardening. The runtime-deps image already
# contains an `app` user (UID/GID 1654) but the playbook spec asks for
# UID 1000 explicitly so it matches the typical RPi `pi` user.
USER 1000

ENTRYPOINT ["./OpenAstroAra.Server"]

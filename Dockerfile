# Dockerfile — OpenAstroAra.Server daemon for linux-arm64 (RPi 4-5)
# Per design/PORT_PLAYBOOK.md §11.2 + §13 deployment target.
#
# Build context expects ./publish/arm64/ to already contain the self-contained
# .NET publish output. CI's `publish` step produces that via:
#   dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 \
#     --self-contained -p:PublishAot=false -o ./publish/arm64
#
# Base: runtime-deps chiseled — because --self-contained bundles the
# .NET + ASP.NET Core runtime DLLs into the publish output, so the base
# image only needs the OS-level runtime dependencies (libc, libssl, ICU
# data, etc). The playbook §11.2 example specifies
# `mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm-slim-arm64v8`
# but Microsoft no longer publishes Debian-based images for .NET 10 —
# only `noble` (Ubuntu 24.04 LTS) and `azurelinux3.0`. We pick
# `noble-chiseled` (the distroless variant) here: ~12MB, no shell, no
# package manager, only the .NET runtime deps. Per the 2026-05-26
# decision in design/PORT_DECISIONS.md, this is the new base for
# OpenAstroAra.Server. Despite the §13 RPi target being Debian, Docker
# image base OS is independent of host OS — the chiseled image runs
# fine on Debian-host containers.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-arm64v8

WORKDIR /app
COPY publish/arm64/ ./

# Default Kestrel port per OpenAstroAra.Server/Program.cs ResolvePort:
# env OPENASTROARA_PORT > appsettings OpenAstroAra:Port > 5555 default.
# 5555 matches the daemon's actual listen port and the Playbook §11.2 example.
EXPOSE 5555

# Non-root per §13 deployment hardening. Chiseled images don't ship an
# /etc/passwd, but `USER <numeric-uid>` works regardless — the kernel
# uses the numeric ID directly. UID 1000 matches the typical RPi `pi`
# user per playbook §11.2.
USER 1000

ENTRYPOINT ["./OpenAstroAra.Server"]

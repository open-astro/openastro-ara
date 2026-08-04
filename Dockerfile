# Dockerfile — OpenAstroAra.Server daemon for linux-arm64 (RPi 4-5)
# Per design/PORT_PLAYBOOK.md §11.2 + §13 deployment target.
#
# Build context expects ./publish/arm64/ to already contain the self-contained
# .NET publish output. CI's `publish` step produces that via:
#   dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 \
#     --self-contained -p:PublishAot=false -o ./publish/arm64
#
# The self-contained publish bundles .NET, but FITS and camera-RAW decoding
# intentionally use distro-maintained native libraries. A normal Noble base
# permits deterministic installation of both dependencies; the former
# chiseled image booted but could not execute either image decoder.
FROM ubuntu:24.04

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libcfitsio10t64 \
        libraw23t64 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY publish/arm64/ ./

# Default Kestrel port per OpenAstroAra.Server/Program.cs ResolvePort:
# env OPENASTROARA_PORT > appsettings OpenAstroAra:Port > 5555 default.
# 5555 matches the daemon's actual listen port and the Playbook §11.2 example.
EXPOSE 5555

# Non-root per §13 deployment hardening. Numeric UID keeps the image
# independent from a particular distribution account name.
USER 1000

ENTRYPOINT ["./OpenAstroAra.Server"]

# Disposable verification image; not the product's deployment/installer definition.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 libgomp1 \
    && rm -rf /var/lib/apt/lists/*
COPY whisper-b5454-linux-x64.tar.gz /tmp/whisper.tar.gz
RUN mkdir -p /opt/whisper && tar -xzf /tmp/whisper.tar.gz --strip-components=1 -C /opt/whisper \
    && rm /tmp/whisper.tar.gz && /opt/whisper/whisper-cli --help
ENV LD_LIBRARY_PATH=/opt/whisper
WORKDIR /app
USER 1654:1654
ENTRYPOINT ["dotnet", "/app/SocialTelemetry.Api.dll"]

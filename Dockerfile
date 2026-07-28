FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Immich.ToGPhoto.App/Immich.ToGPhoto.App.csproj", "Immich.ToGPhoto.App/"]
COPY ["GPMC/GPMC.csproj", "GPMC/"]
COPY ["Immich.Client/Immich.Client.csproj", "Immich.Client/"]
RUN dotnet restore "./Immich.ToGPhoto.App/Immich.ToGPhoto.App.csproj"

COPY . .

WORKDIR /src/Immich.ToGPhoto.App
RUN dotnet publish \
    -c Release \
    -o /publish \
    /p:UseAppHost=false \
    /p:DockerBuild=true

FROM mcr.microsoft.com/dotnet/runtime:10.0

RUN apt-get update && \
    apt-get install -y \
        python3 \
        python3-pip \
        curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /publish ./dotnet

COPY GPMC/Server ./python/
RUN ls -la ./python
RUN cat ./python/requirements.txt
RUN pip3 install --break-system-packages --no-cache-dir -r ./python/requirements.txt

COPY start.sh .
RUN chmod +x start.sh

ENTRYPOINT ["./start.sh"]
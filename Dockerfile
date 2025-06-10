# Learn about building .NET container images:
# https://github.com/dotnet/dotnet-docker/blob/main/samples/README.md
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Copy project file and restore as distinct layers
COPY --link src/*.csproj .
RUN dotnet restore

# Copy source code and publish app
COPY --link src/. .
RUN dotnet publish -c Release --no-restore -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
EXPOSE 8496
WORKDIR /app
COPY --link --from=build /app .
ENTRYPOINT ["./ServerShellEx"]

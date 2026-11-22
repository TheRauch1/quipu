# Builds Quipu.exe (Windows x64, self-contained, single file) on the Linux .NET SDK.
#   docker build --target test .                                      run the unit tests
#   docker build --target export --output type=local,dest=dist .      build dist/Quipu.exe
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# restore in its own layer so package downloads are cached between builds
COPY src/Quipu/Quipu.csproj src/Quipu/
RUN dotnet restore src/Quipu/Quipu.csproj -r win-x64

COPY src/ src/
ARG VERSION=1.0.0
RUN dotnet publish src/Quipu/Quipu.csproj \
      -c Release -r win-x64 --self-contained true --no-restore \
      -p:Version=${VERSION} \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none \
      -o /out
COPY LICENSE THIRD-PARTY-NOTICES.txt /out/

# unit tests (platform-neutral parts of the app, compiled into a Linux test project)
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS test
WORKDIR /src
COPY global.json ./
COPY tests/Quipu.Tests/Quipu.Tests.csproj tests/Quipu.Tests/
RUN dotnet restore tests/Quipu.Tests/Quipu.Tests.csproj
COPY src/ src/
COPY tests/ tests/
RUN dotnet test --project tests/Quipu.Tests/Quipu.Tests.csproj --no-restore -c Release

# only the build output, so `--output` drops just Quipu.exe (+ notices) into ./dist
FROM scratch AS export
COPY --from=build /out/ /

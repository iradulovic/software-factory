FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG FACTORY_PRODUCT_VERSION=Development
ARG FACTORY_SOURCE_COMMIT=unknown
ARG FACTORY_BUILD_TIME_UTC=
ARG FACTORY_BUILD_STATE=development
COPY . .
RUN dotnet publish src/Factory.Api/Factory.Api.csproj -c Release -o /app \
    -p:FactoryProductVersion="$FACTORY_PRODUCT_VERSION" \
    -p:FactorySourceCommit="$FACTORY_SOURCE_COMMIT" \
    -p:FactoryBuildTimeUtc="$FACTORY_BUILD_TIME_UTC" \
    -p:FactoryBuildState="$FACTORY_BUILD_STATE"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/database ./database
ENTRYPOINT ["dotnet", "Factory.Api.dll"]

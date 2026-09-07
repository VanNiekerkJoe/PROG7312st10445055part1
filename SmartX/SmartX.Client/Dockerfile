FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.Core/SmartX.Core.csproj SmartX.Core/
COPY SmartX.Client/SmartX.Client.csproj SmartX.Client/
RUN dotnet restore SmartX.Client/SmartX.Client.csproj
COPY SmartX.Core/ SmartX.Core/
COPY SmartX.Client/ SmartX.Client/
RUN dotnet publish SmartX.Client/SmartX.Client.csproj -c Release -o /app

FROM nginx:alpine AS runtime
COPY --from=build /app/wwwroot /usr/share/nginx/html
EXPOSE 80

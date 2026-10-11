FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.Core/SmartX.Core.csproj SmartX.Core/
COPY SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj SmartX.DeviceSimulator/
RUN dotnet restore SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj
COPY SmartX.Core/ SmartX.Core/
COPY SmartX.DeviceSimulator/ SmartX.DeviceSimulator/
RUN dotnet publish SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SmartX.DeviceSimulator.dll"]

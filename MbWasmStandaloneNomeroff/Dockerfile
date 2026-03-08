# Сборка Blazor WASM
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MbWasmStandaloneNomeroff.csproj", "."]
RUN dotnet restore "MbWasmStandaloneNomeroff.csproj"
COPY . .
RUN dotnet publish "MbWasmStandaloneNomeroff.csproj" -c Release -o /app/publish

# Образ для раздачи статики
FROM nginx:alpine AS runtime
WORKDIR /usr/share/nginx/html
COPY --from=build /app/publish .
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]

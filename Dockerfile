FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LineBotLogger.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
# 容器不允許 inotify 監看設定檔,FileSystemWatcher 會讓程式啟動時 SIGSEGV(exit 139)
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
# Render 執行期才給 PORT,必須在 shell 展開;本機沒 PORT 時退回 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet LineBotLogger.dll"]

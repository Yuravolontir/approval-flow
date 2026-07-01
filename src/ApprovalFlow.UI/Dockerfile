FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/ApprovalFlow.UI/ApprovalFlow.UI.csproj", "src/ApprovalFlow.UI/"]
RUN dotnet restore "src/ApprovalFlow.UI/ApprovalFlow.UI.csproj"
COPY . .
WORKDIR "/src/src/ApprovalFlow.UI"
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ApprovalFlow.UI.dll"]

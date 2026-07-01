FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/ApprovalFlow.Invoice/ApprovalFlow.Invoice.csproj", "src/ApprovalFlow.Invoice/"]
COPY ["src/ApprovalFlow.Shared/ApprovalFlow.Shared.csproj", "src/ApprovalFlow.Shared/"]
RUN dotnet restore "src/ApprovalFlow.Invoice/ApprovalFlow.Invoice.csproj"
COPY . .
WORKDIR "/src/src/ApprovalFlow.Invoice"
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ApprovalFlow.Invoice.dll"]

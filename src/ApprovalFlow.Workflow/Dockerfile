FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/ApprovalFlow.Workflow/ApprovalFlow.Workflow.csproj", "src/ApprovalFlow.Workflow/"]
COPY ["src/ApprovalFlow.Shared/ApprovalFlow.Shared.csproj", "src/ApprovalFlow.Shared/"]
RUN dotnet restore "src/ApprovalFlow.Workflow/ApprovalFlow.Workflow.csproj"
COPY . .
WORKDIR "/src/src/ApprovalFlow.Workflow"
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# Expense policy text for the LLM agent (read at startup from content root)
COPY policy.md .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ApprovalFlow.Workflow.dll"]

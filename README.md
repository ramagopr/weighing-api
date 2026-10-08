# Weighing API: local testing and Azure DevOps deployment

This lab creates a small C#/.NET 8 API, tests it on a Mac, packages it, and demonstrates an Azure DevOps build/deploy pipeline.

**Azure DevOps automates delivery; Azure App Service hosts the application.** Moving desktop code into a pipeline does not automatically convert it into a cloud application. The sample API represents a possible cloud backend, not a completed desktop migration.

## 1. Create the application locally

Prerequisites: VS Code, Git and .NET 8 SDK. Check the SDK:

```bash
dotnet --info
mkdir -p ~/dotnet-lab
cd ~/dotnet-lab
dotnet new web -n WeighingApi --framework net8.0
cd WeighingApi
code .
```

- `Program.cs` contains the application entry point and endpoints.
- `WeighingApi.csproj` defines the target framework and project dependencies.

## 2. Add the endpoints

Replace `Program.cs` in VS Code and save:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Weighing API is running");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/weight", () => Results.Ok(new
{
    deviceId = "SCALE-001",
    weightKg = 1250.5,
    timestamp = DateTime.UtcNow
}));

app.Run();
```

`MapGet` defines HTTP GET endpoints, similar to `@app.get()` in FastAPI. Weight data is synthetic; nothing is stored or read from a scale. `/health` checks responsiveness only, not database or device connectivity.

## 3. Run and test locally

In the VS Code integrated terminal, from `WeighingApi`:

```bash
dotnet run --no-launch-profile --urls http://localhost:5050
```

In a second terminal:

```bash
curl http://localhost:5050/health
curl http://localhost:5050/weight
```

Expected responses:

```json
{"status":"healthy"}
```

```json
{"deviceId":"SCALE-001","weightKg":1250.5,"timestamp":"<current UTC timestamp>"}
```

## 4. Build and publish

Stop the running application with Ctrl+C. From the project folder:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet publish --configuration Release --no-build --output ./publish
ls publish
```

- **Restore:** resolves NuGet dependencies.
- **Build:** compiles C# into `WeighingApi.dll`.
- **Publish:** collects deployable files; it does not upload them to Azure.

Key output files:

| File | Purpose |
| --- | --- |
| `WeighingApi.dll` | Compiled application |
| `WeighingApi.deps.json` | Dependency information |
| `WeighingApi.runtimeconfig.json` | Runtime requirements |
| `appsettings.json` | Application configuration |
| `WeighingApi.pdb` | Debug symbols |
| `web.config` | IIS hosting configuration |

This is a framework-dependent package: the host needs the appropriate .NET/ASP.NET Core runtime. The native launcher produced locally is Mac-specific; the pipeline below builds on Linux for Linux hosting.

Verify the published DLL:

```bash
dotnet ./publish/WeighingApi.dll --urls http://localhost:5050
```

Repeat the two curl commands in another terminal. Both returned the expected JSON in our lab.

## 5. Store source in GitHub

We created a dedicated repository because the original Git root was the home directory. For a new project without its own repository:

```bash
git init -b main
```

Create `.gitignore` in VS Code:

```gitignore
bin/
obj/
publish/
.vscode/
.DS_Store
```

Commit source files:

```bash
git add .gitignore Program.cs Properties/ WeighingApi.csproj appsettings*.json
git commit -m "Add sample weighing API"
```

Create an empty GitHub repository without an initial README, licence or gitignore. Our repository is `https://github.com/ramagopr/weighing-api`.

```bash
git remote add origin https://github.com/ramagopr/weighing-api.git
git push -u origin main
```

If `origin` already exists, inspect it with `git remote -v`; use `git remote set-url origin <correct-url>` only if it needs correcting. Do not commit secrets.

## 6. Prepare Azure DevOps and Azure hosting

1. Sign into https://dev.azure.com/ and create or choose a private lab project.
2. Ensure the project has an available hosted parallel job or a configured self-hosted agent.
3. Provision a Linux Azure App Service and App Service plan with the .NET 8 runtime. Choose a globally unique app name; `weighing-api-dev` below is a placeholder.
4. In Project settings → Service connections, create an Azure Resource Manager connection, preferably using workload identity federation. Scope its permissions to the required deployment resources and authorise this pipeline to use it.
5. In Pipelines → Environments, create `weighing-dev`. Configure any required approvals under Approvals and checks.
6. Select Pipelines → New pipeline → GitHub, connect only the required repository, and choose Existing Azure Pipelines YAML file after adding the file below.

The App Service, service connection and environment are separate resources. The following pipeline deploys application files; it does not provision infrastructure. Configuration and secrets should be supplied through App Service settings and, where appropriate, Key Vault references.

## 7. Complete build and deployment pipeline

Create `azure-pipelines.yml` at the repository root. Replace the service connection and App Service names:

```yaml
trigger:
  - main

pool:
  vmImage: ubuntu-latest

variables:
  buildConfiguration: Release
  azureServiceConnection: azure-dev-connection
  webAppName: weighing-api-dev

stages:
  - stage: Build
    displayName: Build and package
    jobs:
      - job: BuildApi
        steps:
          - checkout: self

          - task: UseDotNet@2
            displayName: Install .NET 8 SDK
            inputs:
              packageType: sdk
              version: '8.x'

          - script: dotnet restore WeighingApi.csproj
            displayName: Restore dependencies

          - script: >
              dotnet build WeighingApi.csproj
              --configuration $(buildConfiguration)
              --no-restore
            displayName: Build application

          - script: >
              dotnet publish WeighingApi.csproj
              --configuration $(buildConfiguration)
              --no-build
              --output "$(Build.ArtifactStagingDirectory)/app"
            displayName: Package application

          - task: PublishPipelineArtifact@1
            displayName: Upload deployment artifact
            inputs:
              targetPath: '$(Build.ArtifactStagingDirectory)/app'
              artifact: weighing-api

  - stage: DeployDev
    displayName: Deploy to development
    dependsOn: Build
    condition: |
      and(
        succeeded(),
        eq(variables['Build.SourceBranch'], 'refs/heads/main')
      )
    jobs:
      - deployment: DeployWeighingApi
        environment: weighing-dev
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: none
                - download: none
                - download: current
                  artifact: weighing-api

                - task: AzureWebApp@1
                  displayName: Deploy to Azure App Service
                  inputs:
                    azureSubscription: '$(azureServiceConnection)'
                    appType: webAppLinux
                    appName: '$(webAppName)'
                    runtimeStack: 'DOTNETCORE|8.0'
                    package: '$(Pipeline.Workspace)/weighing-api'

                - script: |
                    curl --fail --show-error \
                      --retry 6 --retry-delay 10 --retry-all-errors \
                      --max-time 30 \
                      "https://$(webAppName).azurewebsites.net/health"
                  displayName: Verify application health
```

Commit and push the README and pipeline. Azure DevOps runs restore → build → publish → upload artifact → download artifact → deploy → health check. A failed health check fails the job; it does not automatically roll back the deployment.

The local lab was verified. This Azure pipeline is an illustrative configuration and was not executed in our session because Azure DevOps access was unavailable. There is no automated test project yet; add tests before treating this as a production pipeline.

## 8. Apply this to a real desktop-to-cloud migration

1. **Assess the desktop application:** identify .NET Framework versus modern .NET, WinForms/WPF UI, local files, databases, Windows dependencies and scale/device drivers. Older Windows applications may need Windows agents and MSBuild.
2. **Choose the migration approach:** retain the desktop UI and move backend functions into APIs, redesign the UI as a web app, or use a suitable hosted desktop approach. App Service cannot directly host a WinForms/WPF desktop UI.
3. **Separate responsibilities:** extract suitable business logic into backend services. Hardware communication may need to remain in a local component that securely sends readings to the cloud.
4. **Design cloud services:** choose hosting, database, identity, networking and monitoring. Plan offline behaviour, retries and duplicate-reading prevention when devices lose connectivity.
5. **Validate locally:** build the backend, test APIs and business rules, and test the desktop/device integration. Our sample GET endpoint is only a learning scaffold.
6. **Establish CI:** keep source in GitHub or Azure Repos; automate restore, build, tests and packaging. Version artifacts and retain them for promotion and rollback.
7. **Establish CD:** provision infrastructure through Terraform/Bicep; deploy the same tested artifact through dev, test and production with environment-specific configuration and approvals.
8. **Validate and cut over:** run integration, security and performance tests; migrate data with reconciliation; pilot real devices/users; define a rollback plan before switching production traffic.

## References

- [Create your first Azure pipeline](https://learn.microsoft.com/en-us/azure/devops/pipelines/create-first-pipeline?view=azure-devops)
- [Azure Pipelines and GitHub](https://learn.microsoft.com/en-us/azure/devops/pipelines/repos/github?view=azure-devops)
- [Azure App Service deployment task](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-web-app-v1?view=azure-pipelines)
- [Configure CI/CD for Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/deploy-azure-pipelines)

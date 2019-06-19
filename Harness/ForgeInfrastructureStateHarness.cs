using System;
using System.Globalization;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Fathym;
using Fathym.Business.Models;
using Fathym.Design.Singleton;
using LCU.Graphs;
using LCU.Graphs.Registry.Enterprises;
using LCU.Graphs.Registry.Enterprises.Apps;
using LCU.Graphs.Registry.Enterprises.IDE;
using LCU.Graphs.Registry.Enterprises.Identity;
using LCU.Graphs.Registry.Enterprises.Provisioning;
using LCU.Runtime;
using LCU.State.API.Forge.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.IO;
using LibGit2Sharp;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Organization.Client;
using Microsoft.VisualStudio.Services.Organization;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Clients;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts.Conditions;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using System.Threading;
using System.Diagnostics;
using System.Net;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Fathym.Design;

namespace LCU.State.API.Forge.Infrastructure.Harness
{
    public class VssOAuthHandler : DelegatingHandler
    {
        protected readonly string accessToken;

        public VssOAuthHandler(string accessToken)
            : base()
        {
            this.accessToken = accessToken;
        }

        protected async override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("Authorization", $"Bearer {accessToken}");

            var response = await base.SendAsync(request, cancellationToken);

            return response;
        }
    }

    public class ForgeInfrastructureStateHarness : LCUStateHarness<ForgeInfrastructureState>
    {
        #region Fields
        protected readonly BuildHttpClient bldClient;

        protected readonly string container;

        protected readonly ReleaseHttpClient rlsClient;

        protected readonly ProjectHttpClient projClient;

        protected readonly TaskAgentHttpClient taskClient;

        protected readonly ServiceEndpointHttpClient seClient;

        protected readonly string cmdExePath;

        protected readonly string cmdPathVariable;


        protected readonly string devOpsToken;

        protected readonly VssConnection devOpsConn;

        protected readonly Octokit.GitHubClient gitHubClient;

        protected readonly string gitHubToken;

        protected readonly IDGraph idGraph;

        protected readonly IDEGraph ideGraph;

        protected readonly PrvGraph prvGraph;
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        {
            cmdExePath = System.Environment.GetEnvironmentVariable("CMD-EXE-PATH") ?? "cmd";

            cmdPathVariable = System.Environment.GetEnvironmentVariable("CMD-PATH-VARIABLE") ?? null;

            idGraph = req.LoadGraph<IDGraph>(log);

            prvGraph = req.LoadGraph<PrvGraph>(log);

            devOpsToken = idGraph.RetrieveThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "AZURE-DEV-OPS").Result;

            if (!devOpsToken.IsNullOrEmpty())
            {
                // devOpsConn = new VssConnection(new Uri("https://dev.azure.com/fathym"), new VssBasicCredential("", devOpsToken));
                devOpsConn = new VssConnection(new Uri("https://dev.azure.com/fathym"), new VssHttpMessageHandler(new VssOAuthAccessTokenCredential(devOpsToken), VssClientHttpRequestSettings.Default),
                new DelegatingHandler[] {
                    new VssOAuthHandler(devOpsToken)
                });

                bldClient = devOpsConn.GetClient<BuildHttpClient>();

                rlsClient = devOpsConn.GetClient<ReleaseHttpClient>();

                projClient = devOpsConn.GetClient<ProjectHttpClient>();

                taskClient = devOpsConn.GetClient<TaskAgentHttpClient>();

                seClient = devOpsConn.GetClient<ServiceEndpointHttpClient>();

                gitHubToken = idGraph.RetrieveThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "GIT-HUB").Result;

                if (!gitHubToken.IsNullOrEmpty())
                {
                    //  TODO:  Investigate and re-enable GitHub API Caching and eTag handling for Rate Limiting purposes

                    // var client = new Octokit.Caching.CachingHttpClient(new Octokit.Internal.HttpClientAdapter(() => Octokit.Internal.HttpMessageHandlerFactory.CreateDefault(new WebProxy())), 
                    //     new Octokit.Caching.NaiveInMemoryCache());

                    // var connection = new Octokit.Connection(
                    //     new Octokit.ProductHeaderValue("LCU-STATE-API-FORGE-INFRASTRUCTURE"),
                    //     Octokit.GitHubClient.GitHubApiUrl,
                    //     new Octokit.Internal.InMemoryCredentialStore(new Octokit.Credentials(gitHubToken)),
                    //     client,
                    //     new Octokit.Internal.SimpleJsonSerializer());

                    // gitHubClient = new Octokit.GitHubClient(connection);

                    gitHubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("LCU-STATE-API-FORGE-INFRASTRUCTURE"));

                    var tokenAuth = new Octokit.Credentials(gitHubToken);

                    gitHubClient.Credentials = tokenAuth;
                }
            }

            container = "Default";

            ideGraph = req.LoadGraph<IDEGraph>(log);
        }
        #endregion

        #region API Methods
        public virtual async Task<ForgeInfrastructureState> AppSeedCompleteCheck(string filesRoot)
        {
            var appSeed = state.AppSeed.Options.FirstOrDefault(o => o.Lookup == state.AppSeed.SelectedSeed);

            var project = await getOrCreateDevOpsProject();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoPath = Path.Combine(filesRoot, $"git\\repos\\{repoOrg}\\{state.AppSeed.NewName}");

            var repoDir = new DirectoryInfo(repoPath);

            var existingDefs = await bldClient.GetDefinitionsAsync(project.Id.ToString());

            var infraBuildDef = existingDefs.FirstOrDefault(bd => bd.Name == $"{repoOrg} {repoName}");

            var infraBuilds = await bldClient.GetBuildsAsync(project.Id.ToString(), new[] { infraBuildDef.Id });

            var infraBuild = infraBuilds.FirstOrDefault();

            state.AppSeed.InfraBuilt = infraBuild.Status == BuildStatus.Completed;

            var appSeedRepo = await getOrCreateRepository(repoOrg, state.AppSeed.NewName);

            await ensureRepo(repoDir, appSeedRepo.CloneUrl);

            var lcuJson = repoDir.GetFiles("lcu.json")?.FirstOrDefault();

            state.AppSeed.AppSeeded = lcuJson != null && lcuJson.Exists;

            var appSeedBuildDef = existingDefs.FirstOrDefault(bd => bd.Name == $"{repoOrg} {state.AppSeed.NewName}");

            state.AppSeed.HasBuild = appSeedBuildDef != null;

            if (state.AppSeed.HasBuild)
            {
                var appSeedBuilds = await bldClient.GetBuildsAsync(project.Id.ToString(), new[] { appSeedBuildDef.Id });

                var appSeedBuild = infraBuilds.FirstOrDefault();

                state.AppSeed.AppSeedBuilt = appSeedBuild.Status == BuildStatus.Completed;
            }

            var apps = await appGraph.ListApplications(details.EnterpriseAPIKey);

            var appSeedApp = apps.FirstOrDefault(app => app.PathRegex == $"/{repoOrg}/{state.AppSeed.NewName}*");

            if (appSeedApp != null)
            {
                var dafApp = await appGraph.GetDAFApplications(details.EnterpriseAPIKey, appSeedApp.ID);

                state.AppSeed.Step = ForgeInfrastructureApplicationSeedStepTypes.Created;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> CommitInfrastructure(string filesRoot)
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            state.InfraTemplate.Options = new List<string>();

            if (!repoOrg.IsNullOrEmpty())
            {
                var repo = await gitHubClient.Repository.Get(repoOrg, repoName);

                var repoPath = Path.Combine(filesRoot, $"git\\repos\\{repoOrg}\\{repoName}");

                var repoDir = new DirectoryInfo(repoPath);

                await ensureRepo(repoDir, repo.CloneUrl);

                var topLevelDirs = repoDir.GetDirectories();

                var modulesDir = topLevelDirs.FirstOrDefault(d => d.Name == "modules");

                //  TODO:  Load Modules into state

                var templatesDir = topLevelDirs.FirstOrDefault(d => d.Name == "templates");

                var templateFile = templatesDir.GetFiles($"{state.InfraTemplate.SelectedTemplate}\\*", SearchOption.AllDirectories).FirstOrDefault();

                var envPath = Path.Combine(repoDir.FullName, "environments", state.Environment.Lookup);

                var tmplPath = Path.Combine(envPath, "template.json");

                if (!Directory.Exists(envPath))
                    Directory.CreateDirectory(envPath);
                else if (File.Exists(tmplPath))
                    File.Delete(tmplPath);

                templateFile.CopyTo(tmplPath);

                var credsProvider = loadCredHandler();

                await commitAndSync($"Updating infrastrucure from template {state.InfraTemplate.SelectedTemplate}", repoPath, credsProvider);
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ConfigureDevOps(string npmRegistry, string npmAccessToken)
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            var project = await getOrCreateDevOpsProject();

            var repo = await gitHubClient.Repository.Get(repoOrg, repoName);

            var azure = Azure.Authenticate(getAuthorization());

            var azureSub = azure.Subscriptions.GetById(state.EnvSettings.Metadata["AzureSubID"].ToString());

            state.DevOps.NPMRegistry = npmRegistry;

            state.DevOps.NPMAccessToken = npmAccessToken;

            var endpoints = await ensureDevOpsServiceEndpoints(project.Id.ToString(), state.EnvSettings.Metadata["AzureSubID"].ToString(), azureSub.DisplayName);

            var taskGroups = await ensureDevOpsTaskLibrary(project.Id.ToString(), endpoints);

            var gitHubCSId = endpoints["github"];

            var azureRMCSId = endpoints["azurerm"];

            var process = new DesignerProcess();

            var steps = new List<BuildDefinitionStep>()
            {
                new BuildDefinitionStep()
                {
                    AlwaysRun = false,
                    Condition = "succeeded()",
                    ContinueOnError = false,
                    DisplayName = "Copy Templates",
                    Enabled = true,
                    Inputs = new Dictionary<string, string>()
                    {
                        { "CleanTargetFolder", "false" },
                        { "Contents", @"**\*.json" },
                        { "OverWrite", "false" },
                        { "SourceFolder", "environments" },
                        { "TargetFolder", "$(build.artifactstagingdirectory)" },
                        { "flattenFolders", "false" }
                    },
                    RefName = "Copy_Templates",
                    TaskDefinition = new Microsoft.TeamFoundation.Build.WebApi.TaskDefinitionReference()
                    {
                        DefinitionType= "task",
                        VersionSpec = "2.*",
                        Id = new Guid("5bfb729a-a7c8-4a78-a7c3-8d717bb7c13c")
                    }
                },
                new BuildDefinitionStep()
                {
                    AlwaysRun = false,
                    Condition = "succeeded()",
                    ContinueOnError = false,
                    DisplayName = "Publish Artifact: drop",
                    Enabled = true,
                    Inputs = new Dictionary<string, string>()
                    {
                        { "ArtifactName", "drop" },
                        { "ArtifactType", "Container" },
                        { "Parallel", "false" },
                        { "ParallelCount", "8" },
                        { "PathtoPublish", "$(Build.ArtifactStagingDirectory)" },
                        { "TargetPath", "" }
                    },
                    RefName = "Publish_Artifact",
                    TaskDefinition = new Microsoft.TeamFoundation.Build.WebApi.TaskDefinitionReference()
                    {
                        DefinitionType= "task",
                        VersionSpec = "1.*",
                        Id = new Guid("2ff763a7-ce83-4e1f-bc89-0ae63477cebe")
                    }
                }
            };

            var phase = createBuildPhase(steps);

            process.Phases.Add(phase);

            var buildDef = createBuildDefinition(project, process, repo, repoOrg, repoName, gitHubCSId);

            buildDef = await bldClient.CreateDefinitionAsync(buildDef);

            var safeEnvName = state.Environment.Lookup.Replace("-", String.Empty);

            var tasks = new List<WorkflowTask>()
                {
                    new WorkflowTask()
                    {
                        AlwaysRun = false,
                        Condition = "succeeded()",
                        ContinueOnError = false,
                        DefinitionType = "task",
                        Enabled = true,
                        Inputs = new Dictionary<string, string>()
                        {
                            { "ConnectedServiceName", azureRMCSId },
                            { "action", "Create Or Update Resource Group" },
                            { "addSpnToEnvironment", "false" },
                            { "copyAzureVMTags", "true" },
                            { "csmFile", $"$(System.DefaultWorkingDirectory)/_{repoOrg} {repoName}/drop/{state.Environment.Lookup}/template.json" },
                            { "deploymentMode", "Incremental" },
                            { "enableDeploymentPrerequisites", "None" },
                            { "location", "West US 2" },  //    TODO:  Should come configured
                            { "overrideParameters", $"-name {state.Environment.Lookup} -safename {safeEnvName}" },
                            { "resourceGroupName", state.Environment.Lookup },
                            { "runAgentServiceAsUser", "false" },
                            { "templateLocation", "Linked artifact" }
                        },
                        Name = "Azure Deployment:Create Or Update Resource Group action",
                        TaskId = new Guid("94a74903-f93f-4075-884f-dc11f34058b4"),
                        Version = "2.*"
                    }
                };

            var releaseDef = createBuildRelease(project, buildDef, tasks, repoOrg, repoName, azureRMCSId);

            var release = await rlsClient.CreateReleaseDefinitionAsync(releaseDef, project.Id);

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ConfigureInfrastructure(string infraType, bool useDefaultSettings, MetadataModel settings)
        {
            if (useDefaultSettings && infraType == "Azure")
            {
                var orgLookup = state.GitHub.SelectedOrg;

                var originOrgName = "lowcodeunit";

                var repoName = "infrastructure";

                Octokit.Repository orgInfraRepo = null;

                try
                {
                    orgInfraRepo = await gitHubClient.Repository.Get(orgLookup, repoName);
                }
                catch (Octokit.NotFoundException nfex)
                { }

                if (orgInfraRepo == null)
                {
                    //  TODO: Power with data instead of static....  This way root infra repo can be controlled
                    var forkedRepo = await gitHubClient.Repository.Forks.Create(originOrgName, repoName, new Octokit.NewRepositoryFork()
                    {
                        Organization = orgLookup
                    });
                }

                settings.Metadata["GitHubRepository"] = repoName;

                settings.Metadata["GitHubOrganization"] = orgLookup;

                state.Environment = await prvGraph.SaveEnvironment(new Graphs.Registry.Enterprises.Provisioning.Environment()
                {
                    EnterprisePrimaryAPIKey = details.EnterpriseAPIKey,
                    Lookup = $"{orgLookup}-prd",
                    Name = $"{orgLookup} Production"
                });

                state.EnvSettings = await prvGraph.SaveEnvironmentSettings(details.EnterpriseAPIKey, state.Environment.Lookup, settings);

                // state.SetupStep = null;
            }
            else
                state.Error = "Only Azure Default Settings are currently supported";

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> CreateAppFromSeed(string filesRoot, string name)
        {
            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            var repoName = state.AppSeed.NewName = name.ToLower();
            //  TODO - Support configured prefix for name inside the AppSeedOption config
            //(state.AppSeed.SelectedSeed == "Angular" ? name : $"lcu-{name}").ToLower();

            // await withOfflineHarness<CreateAppFromSeedRequest, ForgeInfrastructureStateHarness>(async (mgr, reqData) =>
            // {
            //     return await mgr.CompleteAppSeedCreation(filesRoot, repoName);
            // });

            state.AppSeed.Step = ForgeInfrastructureApplicationSeedStepTypes.Creating;

            await CompleteAppSeedCreation(filesRoot, repoName);

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> CompleteAppSeedCreation(string filesRoot, string repoName)
        {
            var appSeed = state.AppSeed.Options.FirstOrDefault(o => o.Lookup == state.AppSeed.SelectedSeed);

            var project = await getOrCreateDevOpsProject();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            await ensureInfrastructureIsBuilt(project, repoOrg);

            var appSeedBuildDef = await ensureBuildForAppSeed(project, repoOrg, repoName);

            await seedRepo(filesRoot, project, appSeedBuildDef, appSeed, repoOrg, repoName);

            await configureForge(repoOrg, repoName, appSeed.ReleasePackageSuffix);

            return state;
        }

        public override void Dispose()
        {
            if (devOpsConn != null)
            {
                devOpsConn.Dispose();

                bldClient.Dispose();

                rlsClient.Dispose();

                projClient.Dispose();

                taskClient.Dispose();

                seClient.Dispose();
            }
        }

        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            if (state.AppSeed == null)
                state.AppSeed = new InfrastructureApplicationSeedState()
                {
                    Options = new List<InfrastructureApplicationSeedOption>()
                };

            if (state.DevOps == null)
                state.DevOps = new DevOpsState();

            if (state.DevOps.NPMRegistry.IsNullOrEmpty())
                state.DevOps.NPMRegistry = "https://registry.npmjs.org/";

            if (state.GitHub == null || gitHubClient == null)
                state.GitHub = new GitHubState();

            if (state.InfraTemplate == null || gitHubClient == null)
                state.InfraTemplate = new InfrastructureTemplateState();

            await HasDevOpsSetup();

            return await WhenAll(
                HasDevOps(),
                GetEnvironments(),
                HasInfrastructure(),
                HasSourceControl(),
                ListGitHubOrganizations(),
                ListGitHubOrgRepos()
            );
        }

        public virtual async Task<ForgeInfrastructureState> GetEnvironments()
        {
            var envs = await prvGraph.ListEnvironments(details.EnterpriseAPIKey);

            if (!envs.IsNullOrEmpty())
            {
                state.Environment = envs.FirstOrDefault();

                state.EnvSettings = await prvGraph.GetEnvironmentSettings(details.EnterpriseAPIKey, state.Environment?.Lookup);
            }
            else
            {
                state.Environment = null;

                state.EnvSettings = null;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasProdConfig(string filesRoot)
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            state.ProductionConfigured = false;

            if (!repoOrg.IsNullOrEmpty())
            {
                var repo = await gitHubClient.Repository.Get(repoOrg, repoName);

                var repoPath = Path.Combine(filesRoot, $"git\\repos\\{repoOrg}\\{repoName}");

                var repoDir = new DirectoryInfo(repoPath);

                await ensureRepo(repoDir, repo.CloneUrl);

                var envPath = Path.Combine(repoDir.FullName, "environments", state.Environment.Lookup);

                var tmplPath = Path.Combine(envPath, "template.json");

                state.ProductionConfigured = Directory.Exists(envPath);
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasDevOps()
        {
            state.DevOps.Configured = !devOpsToken.IsNullOrEmpty();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasDevOpsSetup()
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            TeamProjectReference project = null;

            if (devOpsConn != null)
            {
                var projects = await projClient.GetProjects(ProjectState.All);

                project = projects.FirstOrDefault(p => p.Name == "LCU OS");
            }

            if (project != null)
            {
                var releases = await rlsClient.GetReleaseDefinitionsAsync(project.Id);

                var name = $"{repoOrg} {repoName}";

                var release = releases.FirstOrDefault(r => r.Name == name);

                state.DevOps.Setup = release != null;
            }
            else
            {
                state.DevOps.Setup = false;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasInfrastructure()
        {
            var envs = await prvGraph.ListEnvironments(details.EnterpriseAPIKey);

            state.InfrastructureConfigured = !envs.IsNullOrEmpty() && envs.Any(env =>
            {
                var envConfig = prvGraph.GetEnvironment(details.EnterpriseAPIKey, env.Lookup).Result;

                return envConfig != null;
            });

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasSourceControl()
        {
            state.SourceControlConfigured = !gitHubToken.IsNullOrEmpty();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ListGitHubOrganizations()
        {
            if (gitHubClient != null)
            {
                var orgs = await gitHubClient.Organization.GetAllForCurrent();

                state.GitHub.Organizations = orgs.ToList();
            }
            else
                state.GitHub.Organizations = new List<Octokit.Organization>();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ListGitHubOrgRepos()
        {
            if (gitHubClient != null && !state.GitHub.SelectedOrg.IsNullOrEmpty())
            {
                var repos = await gitHubClient.Repository.GetAllForOrg(state.GitHub.SelectedOrg);

                state.GitHub.OrgRepos = repos.ToList();
            }
            else
                state.GitHub.OrgRepos = new List<Octokit.Repository>();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> LoadInfrastructureRepository(string filesRoot)
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            state.InfraTemplate.Options = new List<string>();

            if (!repoOrg.IsNullOrEmpty())
            {
                var repo = await gitHubClient.Repository.Get(repoOrg, repoName);

                var repoPath = Path.Combine(filesRoot, $"git\\repos\\{repoOrg}\\{repoName}");

                var repoDir = new DirectoryInfo(repoPath);

                await ensureRepo(repoDir, repo.CloneUrl);

                var topLevelDirs = repoDir.GetDirectories();

                var modulesDir = topLevelDirs.FirstOrDefault(d => d.Name == "modules");

                //  TODO:  Load Modules into state

                var templatesDir = topLevelDirs.FirstOrDefault(d => d.Name == "templates");

                var templateFiles = templatesDir.GetFiles("*", SearchOption.AllDirectories);

                state.InfraTemplate.Options = templateFiles.Select(tf =>
                {
                    return tf.DirectoryName.Replace(templatesDir.FullName, String.Empty).Trim('\\');
                }).ToList();

                var appsDir = topLevelDirs.FirstOrDefault(d => d.Name == "applications");

                var appsFiles = appsDir.GetFiles("*", SearchOption.AllDirectories);

                state.AppSeed.Options = appsFiles.Select(af =>
                {
                    var lookup = af.DirectoryName.Replace(appsDir.FullName, String.Empty).Trim('\\');

                    using (var rdr = af.OpenText())
                    {
                        var appSeed = rdr.ReadToEnd().FromJSON<InfrastructureApplicationSeedOption>();

                        appSeed.Lookup = lookup;

                        return appSeed;
                    }
                }).ToList();
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetSelectedInfrastructureTemplate(string template)
        {
            state.InfraTemplate.SelectedTemplate = template;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetSelectedOrg(string org)
        {
            state.GitHub.SelectedOrg = org;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetSetupStep(ForgeInfrastructureSetupStepTypes? step)
        {
            state.SetupStep = step;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetupAppSeed(string seedLookup)
        {
            state.AppSeed.SelectedSeed = seedLookup;

            return state;
        }
        #endregion

        #region Helpers
        protected virtual async Task checkoutAndSync(string repoPath, LibGit2Sharp.Handlers.CredentialsHandler credsProvider)
        {
            using (var gitRepo = new LibGit2Sharp.Repository(repoPath))
            {
                LibGit2Sharp.Commands.Checkout(gitRepo, gitRepo.Branches["master"]);

                var author = await loadGitHubSignature();

                LibGit2Sharp.Commands.Pull(gitRepo, author, new LibGit2Sharp.PullOptions()
                {
                    FetchOptions = new LibGit2Sharp.FetchOptions()
                    {
                        CredentialsProvider = credsProvider
                    }
                });
            }
        }

        protected virtual async Task commitAndSync(string message, string repoPath, LibGit2Sharp.Handlers.CredentialsHandler credsProvider)
        {
            using (var gitRepo = new LibGit2Sharp.Repository(repoPath))
            {
                var author = await loadGitHubSignature();

                Commands.Stage(gitRepo, "*");

                if (gitRepo.RetrieveStatus().IsDirty)
                    gitRepo.Commit(message, author, author, new LibGit2Sharp.CommitOptions());

                var remote = gitRepo.Network.Remotes["origin"];

                var pushRefSpec = $"refs/heads/master";

                gitRepo.Network.Push(remote, pushRefSpec, new PushOptions()
                {
                    CredentialsProvider = credsProvider
                });
            }
        }

        protected virtual async Task configureForge(string repoOrg, string repoName, string releaseSuffix)
        {
            var dataAppsLcu = new LowCodeUnitConfig()
            {
                Lookup = "lcu-data-apps",
                NPMPackage = "@napkin-ide/lcu-data-apps-lcu",
                PackageVersion = "latest"
            };

            await ideGraph.EnsureIDESettings(new IDEContainerSettings()
            {
                Container = container,
                EnterprisePrimaryAPIKey = details.EnterpriseAPIKey
            });

            var daStatus = await ensureApplication(dataAppsLcu);

            dataAppsLcu = await ideGraph.SaveLCU(dataAppsLcu, details.EnterpriseAPIKey, container);

            await deconstructLCUConfig(dataAppsLcu.Lookup);

            var app = await appGraph.Save(new Application()
            {
                Container = "lcu-data-apps",
                EnterprisePrimaryAPIKey = details.EnterpriseAPIKey,
                Hosts = new List<string>() { details.Host },
                IsPrivate = false,
                IsReadOnly = false,
                Name = repoName,
                PathRegex = $"/{repoOrg}/{repoName}*",
                Priority = 25000
            });

            var dafView = new DAFViewConfiguration()
            {
                ApplicationID = app.ID,
                Priority = 1000,
                BaseHref = $"/{repoOrg}/{repoName}/",
                NPMPackage = $"@{repoOrg}/{repoName}{releaseSuffix ?? String.Empty}",
                PackageVersion = "latest"
            };

            var appStatus = await unpackView(dafView, details.EnterpriseAPIKey);

            dafView.PackageVersion = appStatus.Metadata["Version"].ToString();

            var dafApp = await appGraph.SaveDAFApplication(details.EnterpriseAPIKey, dafView.JSONConvert<DAFApplicationConfiguration>());
        }

        protected virtual BuildDefinition createBuildDefinition(TeamProjectReference project, DesignerProcess process, Octokit.Repository repo,
            string repoOrg, string repoName, string gitHubCSId, string defaultBranch = "master")
        {
            var name = $"{repoOrg} {repoName}";

            var buildDef = new BuildDefinition()
            {
                BadgeEnabled = true,
                BuildNumberFormat = "$(MajorVersion).$(MinorVersion).$(Rev:r)",
                Name = name,
                Process = process,
                Project = project,
                Queue = new AgentPoolQueue()
                {
                    Name = "Hosted VS2017",
                    Pool = new Microsoft.TeamFoundation.Build.WebApi.TaskAgentPoolReference()
                    {
                        Name = "Hosted VS2017",
                        IsHosted = true
                    }
                },
                Repository = new BuildRepository()
                {
                    Id = $"{repoOrg}/{repoName}",
                    DefaultBranch = defaultBranch,
                    Name = $"{repoOrg}/{repoName}",
                    Type = "GitHub",
                    Url = new Uri(repo.CloneUrl)
                }
            };

            buildDef.Repository.Properties.Add("apiUrl", $"https://api.github.com/repos/{repoOrg}/{repoName}");

            buildDef.Repository.Properties.Add("archived", "False");

            buildDef.Repository.Properties.Add("branchesUrl", $"https://api.github.com/repos/{repoOrg}/{repoName}/branches");

            buildDef.Repository.Properties.Add("checkoutNestedSubmodules", "false");

            buildDef.Repository.Properties.Add("cleanOptions", "0");

            buildDef.Repository.Properties.Add("cloneUrl", repo.CloneUrl);

            buildDef.Repository.Properties.Add("connectedServiceId", gitHubCSId);

            buildDef.Repository.Properties.Add("defaultBranch", defaultBranch);

            // buildDef.Repository.Properties.Add("externalId", "189437748");

            buildDef.Repository.Properties.Add("fetchDepth", "0");

            buildDef.Repository.Properties.Add("fullName", $"{repoOrg}/{repoName}");

            buildDef.Repository.Properties.Add("gitLfsSupport", "false");

            buildDef.Repository.Properties.Add("hasAdminPermissions", "True");

            // buildDef.Repository.Properties.Add("isFork", "True");

            // buildDef.Repository.Properties.Add("isPrivate", "True");

            buildDef.Repository.Properties.Add("labelSources", "0");

            buildDef.Repository.Properties.Add("labelSourcesFormat", "$(build.buildNumber)");

            buildDef.Repository.Properties.Add("manageUrl", $"https://github.com/{repoOrg}/{repoName}");

            buildDef.Repository.Properties.Add("orgName", repoOrg);

            buildDef.Repository.Properties.Add("refsUrl", $"https://api.github.com/repos/{repoOrg}/{repoName}/git/refs");

            buildDef.Repository.Properties.Add("reportBuildStatus", "true");

            buildDef.Repository.Properties.Add("safeRepository", $"{repoOrg}/{repoName}");

            buildDef.Repository.Properties.Add("shortName", repoName);

            buildDef.Repository.Properties.Add("skipSyncSource", "false");

            var ci = new ContinuousIntegrationTrigger();

            ci.BranchFilters.Add("+master");

            if (defaultBranch != "master")
                ci.BranchFilters.Add($"+{defaultBranch}");

            ci.BranchFilters.Add("+feature/*");

            buildDef.Triggers.Add(ci);

            buildDef.Variables.Add("MajorVersion", new BuildDefinitionVariable()
            {
                Value = "0"
            });

            buildDef.Variables.Add("MinorVersion", new BuildDefinitionVariable()
            {
                Value = "1"
            });

            return buildDef;
        }

        protected virtual Phase createBuildPhase(List<BuildDefinitionStep> steps)
        {
            return new Phase()
            {
                Condition = "succeeded()",
                Name = "Prepare Build",
                RefName = "Prepare_Build",
                JobAuthorizationScope = BuildAuthorizationScope.ProjectCollection,
                Target = new AgentPoolQueueTarget()
                {
                    ExecutionOptions = new AgentTargetExecutionOptions()
                    {
                        Type = 0
                    },
                    AllowScriptsAuthAccessOption = true
                },
                Steps = steps
            };
        }

        protected virtual ReleaseDefinition createBuildRelease(TeamProjectReference project, BuildDefinition buildDef, List<WorkflowTask> tasks,
            string repoOrg, string repoName, string azureRMCSId)
        {
            var name = $"{repoOrg} {repoName}";

            var artifact = new Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts.Artifact()
            {
                Alias = $"_{name}",
                IsPrimary = true,
                IsRetained = false,
                Type = "Build",
                DefinitionReference = new Dictionary<string, ArtifactSourceReference>()
                {
                    {
                        "IsMultiDefinitionType", new ArtifactSourceReference() { Id = "False", Name = "False" }
                    },
                    {
                        "definition", new ArtifactSourceReference() { Id = buildDef.Id.ToString(), Name = name }
                    },
                    {
                        "project", new ArtifactSourceReference() { Id = project.Id.ToString(), Name = project.Name }
                    }
                }
            };

            var releaseDef = new ReleaseDefinition()
            {
                Artifacts = new List<Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts.Artifact>()
                {
                    artifact
                },
                Environments = new List<ReleaseDefinitionEnvironment>()
                {
                    new ReleaseDefinitionEnvironment()
                    {
                        Conditions = new List<Condition>()
                        {
                            new Condition()
                            {
                                ConditionType= ConditionType.Event,
                                Name = "ReleaseStarted",
                                Value = String.Empty
                            }
                        },
                        Name = "Production",
                        PostDeployApprovals = new ReleaseDefinitionApprovals()
                        {
                            Approvals = new List<ReleaseDefinitionApprovalStep>()
                            {
                                new ReleaseDefinitionApprovalStep()
                                {
                                    IsAutomated = true,
                                    IsNotificationOn = false,
                                    Rank = 1
                                }
                            }
                        },
                        PreDeployApprovals = new ReleaseDefinitionApprovals()
                        {
                            Approvals = new List<ReleaseDefinitionApprovalStep>()
                            {
                                new ReleaseDefinitionApprovalStep()
                                {
                                    IsAutomated = true,
                                    IsNotificationOn = false,
                                    Rank = 1
                                }
                            }
                        },
                        RetentionPolicy = new EnvironmentRetentionPolicy()
                        {
                            DaysToKeep = 30,
                            ReleasesToKeep = 3,
                            RetainBuild = true
                        },
                        DeployPhases = new List<DeployPhase>()
                        {
                            new AgentBasedDeployPhase()
                            {
                                DeploymentInput = new AgentDeploymentInput()
                                {
                                    QueueId = buildDef.Queue.Id
                                },
                                Name = "Agent Job",
                                Rank = 1,
                                WorkflowTasks = tasks
                            }
                        }
                    }
                },
                Name = name,
                ReleaseNameFormat = "Release-$(rev:r)",
                Triggers = new List<ReleaseTriggerBase>()
                {
                    new ArtifactSourceTrigger()
                    {
                        ArtifactAlias = $"_{repoOrg} {repoName}",
                        TriggerConditions = null
                    }
                }
            };

            return releaseDef;
        }

        protected virtual async Task deconstructLCUConfig(string lcuLookup)
        {
            var client = new HttpClient();

            //  TODO:  This should hard code at https once that is enforced on the platform
            var lcuJsonPath = $"http://{details.Host}/_lcu/{lcuLookup}/lcu.json";

            log.LogInformation($"Loading lcu.json from: {lcuJsonPath}");

            var lcuConfigResp = await client.GetAsync(lcuJsonPath);

            var lcuConfigStr = await lcuConfigResp.Content.ReadAsStringAsync();

            log.LogInformation($"lcu.json Loaded: {lcuConfigStr}");

            if (lcuConfigResp.IsSuccessStatusCode && !lcuConfigStr.IsNullOrEmpty() && !lcuConfigStr.StartsWith("<"))
            {
                var lcuConfig = lcuConfigStr.FromJSON<dynamic>();

                var slnsDict = ((JToken)lcuConfig.config.solutions).ToObject<Dictionary<string, dynamic>>();

                var solutions = slnsDict.Select(sd => new IdeSettingsConfigSolution()
                {
                    Element = sd.Value.element,
                    Name = sd.Key
                }).ToList();

                var files = await ideGraph.ListLCUFiles(lcuLookup, details.Host);

                //  TODO:  Elements and Modules

                var status = await ideGraph.SaveLCUCapabilities(lcuLookup, files, solutions, details.EnterpriseAPIKey, container);

                var activity = await ideGraph.SaveActivity(new IDEActivity()
                {
                    Icon = "dashboard",
                    Lookup = "core",
                    Title = "Core"
                }, details.EnterpriseAPIKey, container);

                solutions.Each(sln =>
                {
                    var secAct = ideGraph.SaveSectionAction(activity.Lookup, "LCUs", new IdeSettingsSectionAction()
                    {
                        Action = sln.Name,
                        Group = "Applications",
                        Name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sln.Name.Replace('-', ' ')),
                    }, details.EnterpriseAPIKey, container).Result;
                });
            }
        }

        protected virtual async Task<BuildDefinitionReference> ensureBuildForAppSeed(TeamProjectReference project, string repoOrg, string repoName)
        {
            var existingDefs = await bldClient.GetDefinitionsAsync(project.Id.ToString());

            var existingDef = existingDefs.FirstOrDefault(bd => bd.Name == $"{repoOrg} {repoName}");

            if (existingDef == null)
            {
                var azure = Azure.Authenticate(getAuthorization());

                var azureSub = azure.Subscriptions.GetById(state.EnvSettings.Metadata["AzureSubID"].ToString());

                log.LogInformation($"Getting or creationg repository {repoOrg}/{repoName}");

                var repo = await getOrCreateRepository(repoOrg, repoName);

                var endpoints = await ensureDevOpsServiceEndpoints(project.Id.ToString(), state.EnvSettings.Metadata["AzureSubID"].ToString(), azureSub.DisplayName);

                var taskGroups = await ensureDevOpsTaskLibrary(project.Id.ToString(), endpoints);

                var gitHubCSId = endpoints["github"];

                var azureRMCSId = endpoints["azurerm"];

                var process = new DesignerProcess();

                var npmBld = taskGroups.FirstOrDefault(tg => tg.Name == "Low Code Unit - NPM Build");

                var gitLbl = taskGroups.FirstOrDefault(tg => tg.Name == "Low Code Unit - Git Label");

                var steps = new List<BuildDefinitionStep>()
                {
                    new BuildDefinitionStep()
                    {
                        AlwaysRun = true,
                        Condition = "succeededOrFailed()",
                        ContinueOnError = true,
                        DisplayName = npmBld.Name,
                        Enabled = true,
                        Inputs = new Dictionary<string, string>() { },
                        TaskDefinition = new Microsoft.TeamFoundation.Build.WebApi.TaskDefinitionReference()
                        {
                            DefinitionType= "metaTask",
                            VersionSpec = "1.*",
                            Id = npmBld.Id
                        }
                    },
                    new BuildDefinitionStep()
                    {
                        AlwaysRun = true,
                        Condition = "succeededOrFailed()",
                        ContinueOnError = true,
                        DisplayName = gitLbl.Name,
                        Enabled = true,
                        Inputs = new Dictionary<string, string>() { },
                        TaskDefinition = new Microsoft.TeamFoundation.Build.WebApi.TaskDefinitionReference()
                        {
                            DefinitionType= "metaTask",
                            VersionSpec = "1.*",
                            Id = gitLbl.Id
                        }
                    },
                };

                var phase = createBuildPhase(steps);

                process.Phases.Add(phase);

                var buildDef = createBuildDefinition(project, process, repo, repoOrg, repoName, gitHubCSId);

                buildDef = await bldClient.CreateDefinitionAsync(buildDef);

                return buildDef;
            }

            return existingDef;
        }

        protected virtual async Task<List<TaskGroup>> ensureDevOpsTaskLibrary(string projectId, IDictionary<string, string> endpoints)
        {
            var taskGroups = new List<TaskGroup>();

            taskGroups = await taskClient.GetTaskGroupsAsync(projectId, expanded: true);

            if (taskGroups.IsNullOrEmpty())
            {
                var defaultTaskGroups = await loadDefaultTaskGroups(endpoints);

                defaultTaskGroups.ForEach(dtg =>
                {
                    var taskGroup = taskClient.AddTaskGroupAsync(projectId, dtg).Result;

                    taskGroups.Add(taskGroup);
                });
            }

            return taskGroups;
        }

        protected virtual async Task<IDictionary<string, string>> ensureDevOpsServiceEndpoints(string projectId, string azureSubId, string azureSubName)
        {
            var endpoints = new Dictionary<string, string>();

            var ses = await seClient.GetServiceEndpointsAsync(projectId);

            var tasks = new Task[] {
                Task.Run(async () => {
                    var gitHubCSId = ses?.FirstOrDefault(se => se.Type.ToLower() == "github" && se.Name == $"GitHub {azureSubName}")?.Id.ToString();

                    if (gitHubCSId.IsNullOrEmpty())
                    {
                        var ghAccessToken = await idGraph.RetrieveThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "GIT-HUB");

                        var ghSE = await seClient.CreateServiceEndpointAsync(projectId, new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.ServiceEndpoint()
                        {
                            Authorization = new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.EndpointAuthorization()
                            {
                                Parameters = new Dictionary<string, string>()
                                {
                                    { "AccessToken", ghAccessToken }
                                },
                                Scheme = "Token"
                            },
                            Data = new Dictionary<string, string>()
                            {
                                { "AvatarUrl", "http://fathym.com/" }  //   TODO:  HOw to get this?
                            },
                            Name = $"GitHub {azureSubName}",
                            Type = "github",
                            Url = new Uri("https://github.com")
                        });

                        gitHubCSId = ghSE?.Id.ToString();
                    }

                    lock(endpoints)
                        endpoints["github"] = gitHubCSId;
                }),
                Task.Run(async () => {
                    var azureRMCSId = ses?.FirstOrDefault(se => se.Type.ToLower() == "azurerm" && se.Name == $"Azure {azureSubName}")?.Id.ToString();

                    if (azureRMCSId.IsNullOrEmpty())
                    {
                        var se = await seClient.CreateServiceEndpointAsync(projectId, new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.ServiceEndpoint()
                        {
                            Authorization = new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.EndpointAuthorization()
                            {
                                Parameters = new Dictionary<string, string>()
                                {
                                    { "authenticationType", "spnKey" },
                                    { "serviceprincipalid", state.EnvSettings.Metadata["AzureAppID"].ToString() },
                                    { "serviceprincipalkey", state.EnvSettings.Metadata["AzureAppAuthKey"].ToString() },
                                    { "tenantid", state.EnvSettings.Metadata["AzureTenantID"].ToString() }
                                },
                                Scheme = "ServicePrincipal"
                            },
                            Data = new Dictionary<string, string>()
                            {
                                { "environment", "AzureCloud" },
                                { "scopeLevel", "Subscription" },
                                { "subscriptionName", azureSubName },
                                { "subscriptionId", state.EnvSettings.Metadata["AzureSubID"].ToString() }
                            },
                            Name = $"Azure {azureSubName}",
                            Type = "azurerm",
                            Url = new Uri("https://management.azure.com/")
                        });

                        azureRMCSId = se?.Id.ToString();
                    }

                    lock(endpoints)
                        endpoints["azurerm"] = azureRMCSId;
                }),
                Task.Run(async () => {
                    var npmRcId = ses?.FirstOrDefault(se => se.Type.ToLower() == "externalnpmregistry" && se.Name == $"NPM RC {azureSubName}")?.Id.ToString();

                    if (npmRcId.IsNullOrEmpty())
                    {
                        await idGraph.SetThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "NPM-RC-REGISTRY", state.DevOps.NPMRegistry);

                        await idGraph.SetThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "NPM-RC-TOKEN", state.DevOps.NPMAccessToken);

                        var se = await seClient.CreateServiceEndpointAsync(projectId, new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.ServiceEndpoint()
                        {
                            Authorization = new Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi.EndpointAuthorization()
                            {
                                Parameters = new Dictionary<string, string>()
                                {
                                    { "apitoken", state.DevOps.NPMAccessToken }
                                },
                                Scheme = "Token"
                            },
                            Data = new Dictionary<string, string>() { },
                            Name = $"NPM RC {azureSubName}",
                            Type = "externalnpmregistry",
                            Url = new Uri(state.DevOps.NPMRegistry)
                        });

                        npmRcId = se?.Id.ToString();
                    }

                    lock(endpoints)
                        endpoints["npmrc"] = npmRcId;
                })
            };

            tasks.WhenAll();

            return endpoints;
        }

        protected virtual async Task ensureInfrastructureIsBuilt(TeamProjectReference project, string repoOrg)
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var buildDefs = await bldClient.GetDefinitionsAsync(project.Id.ToString());

            var buildDef = buildDefs.FirstOrDefault(bd => bd.Name == $"{repoOrg} {repoName}");

            if (buildDef != null)
            {
                await startBuildAndWait(project, buildDef);

                // await startDep(project, buildDef);
            }
        }

        protected virtual async Task ensureRepo(DirectoryInfo repoDir, string cloneUrl)
        {
            var credsProvider = loadCredHandler();

            if (!repoDir.Exists)
            {
                var cloneOptions = new LibGit2Sharp.CloneOptions()
                {
                    CredentialsProvider = credsProvider,
                    BranchName = "master",
                    Checkout = false
                };

                LibGit2Sharp.Repository.Clone(cloneUrl, repoDir.FullName, cloneOptions);
            }

            await checkoutAndSync(repoDir.FullName, credsProvider);

            using (var gitRepo = new LibGit2Sharp.Repository(repoDir.FullName))
            {
                gitRepo.Reset(ResetMode.Hard, gitRepo.Head.Tip);
            }
        }

        protected virtual AzureCredentials getAuthorization()
        {
            return new AzureCredentials(new ServicePrincipalLoginInformation()
            {
                ClientId = state.EnvSettings.Metadata["AzureAppID"].ToString(),
                ClientSecret = state.EnvSettings.Metadata["AzureAppAuthKey"].ToString()
            }, state.EnvSettings.Metadata["AzureTenantID"].ToString(), AzureEnvironment.AzureGlobalCloud);
        }

        protected virtual async Task<TeamProjectReference> getOrCreateDevOpsProject()
        {
            var projects = await projClient.GetProjects(ProjectState.All);

            var project = projects.FirstOrDefault(p => p.Name == "LCU OS");

            if (project == null)
            {
                var createRef = await projClient.QueueCreateProject(new TeamProject()
                {
                    Name = "LCU OS",
                    Description = "Dev Ops automation and integrations for Low Code Units",
                    Capabilities = new Dictionary<string, Dictionary<string, string>>
                    {
                        {
                            "versioncontrol", new Dictionary<string, string>()
                            {
                                { "sourceControlType", "Git"}
                            }
                        },
                        {
                            "processTemplate", new Dictionary<string, string>()
                            {
                                { "templateTypeId", "6b724908-ef14-45cf-84f8-768b5384da45"}
                            }
                        }
                    },
                    Visibility = ProjectVisibility.Private
                });
            }

            while (project == null || project.State == ProjectState.CreatePending)
            {
                projects = await projClient.GetProjects(ProjectState.All);

                project = projects.FirstOrDefault(p => p.Name == "LCU OS");

                await Task.Delay(100);
            }

            return project;
        }

        protected virtual async Task<Octokit.Repository> getOrCreateRepository(string repoOrg, string repoName)
        {
            var repo = await tryGetRepository(repoOrg, repoName);

            if (repo == null)
            {
                var newRepo = new Octokit.NewRepository(repoName)
                {
                    LicenseTemplate = "mit"
                };

                repo = await gitHubClient.Repository.Create(repoOrg, newRepo);
            }

            return repo;
        }

        protected virtual LibGit2Sharp.Handlers.CredentialsHandler loadCredHandler()
        {
            return (LibGit2Sharp.Handlers.CredentialsHandler)((url, user, cred) =>
            {
                return new LibGit2Sharp.UsernamePasswordCredentials { Username = gitHubToken, Password = "" };
            });
        }

        protected virtual async Task<List<TaskGroupCreateParameter>> loadDefaultTaskGroups(IDictionary<string, string> endpoints)
        {
            var bldCfgInput = new TaskInputDefinition()
            {
                Name = "BuildConfiguration",
                Label = "BuildConfiguration",
                DefaultValue = "release",
                Required = true,
                InputType = "string",
                HelpMarkDown = "Specify the configuration you want to build such as debug or release."
            };

            var bldPltInput = new TaskInputDefinition()
            {
                Name = "BuildPlatform",
                Label = "BuildPlatform",
                DefaultValue = "any cpu",
                Required = true,
                InputType = "string",
                HelpMarkDown = "Specify the platform you want to build such as Win32, x86, x64 or any cpu."
            };

            var prj2PkgInput = new TaskInputDefinition()
            {
                Name = "ProjectsToPackage",
                Label = "ProjectsToPackage",
                DefaultValue = "**/*.csproj",
                Required = true,
                InputType = "filePath",
                HelpMarkDown = "Pattern to search for csproj or nuspec files to pack. You can separate multiple patterns with a semicolon, and you can make a pattern negative by prefixing it with '-:'. Example: `**/*.csproj;-:**/*.Tests.csproj`"
            };

            #region Azure Function
            var azFuncTask = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - Azure Function - Restore, Build, Test, Publish",
                Description = "Nuget Restore, .Net Core build, Test, Publish artifact for deployment",
                IconUrl = "https://cdn.vsassets.io/v/M149_20190409.2/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - Azure Function - Restore, Build, Test, Publish",
                InstanceNameFormat = "Low Code Unit - Azure Function - Restore, Build, Test, Publish | $(BuildConfiguration)"
            };

            azFuncTask.RunsOn.Clear();

            azFuncTask.RunsOn.Add("Agent");

            azFuncTask.RunsOn.Add("DeploymentGroup");

            azFuncTask.Inputs.Add(bldCfgInput);

            azFuncTask.Inputs.Add(bldPltInput);

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Use NuGet 4.9.2",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "versionSpec", "4.9.2" },
                    { "checkLatest", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("2c65196a-54fd-4a02-9be8-d9d1837b7c5d"),
                    VersionSpec = "0.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "NuGet restore",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "restore" },
                    { "solution", @"**\*.csproj" },
                    { "selectOrConfig", "config" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "nuget.config" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "disableParallelProcessing", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/**/*.nupkg;!$(Build.ArtifactStagingDirectory)/**/*.symbols.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "allowPackageConflicts", "false" },
                    { "externalEndpoint", "" },
                    { "verbosityPush", "Detailed" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "versioningScheme", "off" },
                    { "includeReferencedProjects", "false" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "packTimezone", "utc" },
                    { "includeSymbols", "false" },
                    { "toolPackage", "false" },
                    { "buildProperties", "" },
                    { "basePath", "" },
                    { "verbosityPack", "Detailed" },
                    { "arguments", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("333b11bd-d341-40d9-afcf-b32d5ce6f23b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Build solution",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "solution", @"**\*.csproj" },
                    { "vsVersion", "latest" },
                    { "msbuildArgs", @"/p:DeployOnBuild=true /p:DeployDefaultTarget=WebPublish /p:WebPublishMethod=FileSystem /p:publishUrl=""$(Agent.TempDirectory)\WebAppContent\\""" },
                    { "platform", "$(BuildPlatform)" },
                    { "configuration", "$(BuildConfiguration)" },
                    { "clean", "false" },
                    { "maximumCpuCount", "false" },
                    { "restoreNugetPackages", "false" },
                    { "msbuildArchitecture", "x86" },
                    { "logProjectEvents", "true" },
                    { "createLogFile", "false" },
                    { "logFileVerbosity", "normal" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("71a9a2d3-a98a-4caa-96ab-affca411ecda"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Archive Files",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "rootFolderOrFile", @"$(Agent.TempDirectory)\WebAppContent" },
                    { "includeRootFolder", "false" },
                    { "archiveType", "zip" },
                    { "tarCompression", "gz" },
                    { "archiveFile", "$(Build.ArtifactStagingDirectory)/deploy.zip" },
                    { "replaceExistingArchive", "true" },
                    { "verbose", "false" },
                    { "quiet", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("d8b84976-e99a-4b86-b885-4849694435b0"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Test Assemblies",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "testSelector", "testAssemblies" },
                    { "testAssemblyVer2", @"**\*.test.dll !**\obj\**" },
                    { "testPlan", "" },
                    { "testSuite", "" },
                    { "testConfiguration", "" },
                    { "tcmTestRun", "$(test.RunId)" },
                    { "searchFolder", "$(System.DefaultWorkingDirectory)" },
                    { "testFiltercriteria", "" },
                    { "runOnlyImpactedTests", "False" },
                    { "runAllTestsAfterXBuilds", "50" },
                    { "uiTests", "false" },
                    { "vstestLocationMethod", "version" },
                    { "vsTestVersion", "latest" },
                    { "vstestLocation", "" },
                    { "runSettingsFile", "" },
                    { "overrideTestrunParameters", "" },
                    { "pathtoCustomTestAdapters", "" },
                    { "runInParallel", "False" },
                    { "runTestsInIsolation", "False" },
                    { "codeCoverageEnabled", "False" },
                    { "otherConsoleOptions", "" },
                    { "distributionBatchType", "basedOnTestCases" },
                    { "batchingBasedOnAgentsOption", "autoBatchSize" },
                    { "customBatchSizeValue", "10" },
                    { "batchingBasedOnExecutionTimeOption", "autoBatchSize" },
                    { "customRunTimePerBatchValue", "60" },
                    { "dontDistribute", "False" },
                    { "testRunTitle", "" },
                    { "platform", "$(BuildPlatform)" },
                    { "configuration", "$(BuildConfiguration)" },
                    { "publishRunAttachments", "true" },
                    { "diagnosticsEnabled", "True" },
                    { "collectDumpOn", "onAbortOnly" },
                    { "rerunFailedTests", "False" },
                    { "rerunType", "basedOnTestFailurePercentage" },
                    { "rerunFailedThreshold", "30" },
                    { "rerunFailedTestCasesMaxLimit", "5" },
                    { "rerunMaxAttempts", "3" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("ef087383-ee5e-42c7-9a53-ab56c98420f9"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Publish symbols path",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "SymbolsPath", "" },
                    { "SearchPattern", @"**\bin\**\*.pdb" },
                    { "SymbolsFolder", "" },
                    { "SkipIndexing", "false" },
                    { "TreatNotIndexedAsWarning", "false" },
                    { "SymbolsMaximumWaitTime", "" },
                    { "SymbolsProduct", "" },
                    { "SymbolsVersion", "" },
                    { "SymbolsArtifactName", "Symbols_$(BuildConfiguration)"}
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("0675668a-7bba-4ccb-901d-5ad6554ca653"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });

            azFuncTask.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Publish Artifact",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "PathtoPublish", "$(build.artifactstagingdirectory)" },
                    { "ArtifactName", "drop" },
                    { "ArtifactType", "Container" },
                    { "TargetPath", @"\\my\share\$(Build.DefinitionName)\$(Build.BuildNumber)" },
                    { "Parallel", "false" },
                    { "ParallelCount", "8"}
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("2ff763a7-ce83-4e1f-bc89-0ae63477cebe"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region NuGet Restore
            var nugRstr = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Package",
                Name = "Low Code Unit - NuGet Restore (.Net <=4.6)",
                Description = "Nuget Restore",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - NuGet Restore (.Net <=4.6)",
                InstanceNameFormat = "Low Code Unit - NuGet Restore (.Net <=4.6) | $(BuildConfiguration)"
            };

            nugRstr.RunsOn.Clear();

            nugRstr.RunsOn.Add("Agent");

            nugRstr.RunsOn.Add("DeploymentGroup");

            nugRstr.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Use NuGet 4.4.1",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "versionSpec", "4.4.1" },
                    { "checkLatest", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("2c65196a-54fd-4a02-9be8-d9d1837b7c5d"),
                    VersionSpec = "0.*",
                    DefinitionType = "task"
                }
            });

            nugRstr.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "NuGet Restore",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "restore" },
                    { "solution", "**/*.sln" },
                    { "selectOrConfig", "config" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", ".nuget/NuGet.config" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "disableParallelProcessing", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/**/*.nupkg;!$(Build.ArtifactStagingDirectory)/**/*.symbols.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "allowPackageConflicts", "false" },
                    { "externalEndpoint", "" },
                    { "verbosityPush", "Detailed" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "versioningScheme", "off" },
                    { "includeReferencedProjects", "false" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "packTimezone", "utc" },
                    { "includeSymbols", "false" },
                    { "toolPackage", "false" },
                    { "buildProperties", "" },
                    { "basePath", "" },
                    { "verbosityPack", "Detailed" },
                    { "arguments", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("333b11bd-d341-40d9-afcf-b32d5ce6f23b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region .Net Core Package Push
            var netCorePkgPush = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - .Net Core Package Push",
                Description = "Package|Push",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - .Net Core Package Push",
                InstanceNameFormat = "Low Code Unit - .Net Core Package Push | $(BuildConfiguration)"
            };

            netCorePkgPush.RunsOn.Clear();

            netCorePkgPush.RunsOn.Add("Agent");

            netCorePkgPush.Inputs.Add(bldCfgInput);

            netCorePkgPush.Inputs.Add(prj2PkgInput);

            netCorePkgPush.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), ne(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Pack Prerelease",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "pack" },
                    { "publishWebProjects", "true" },
                    { "projects", "" },
                    { "custom", "" },
                    { "arguments", "" },
                    { "publishTestResults", "true" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "select" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "$(ProjectsToPackage)" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "off" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "version=$(Build.BuildNumber)-prerelease" },
                    { "verbosityPack", "Normal" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            netCorePkgPush.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Pack Production",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "pack" },
                    { "publishWebProjects", "true" },
                    { "projects", "" },
                    { "custom", "" },
                    { "arguments", "" },
                    { "publishTestResults", "true" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "select" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "$(ProjectsToPackage)" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "byBuildNumber" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "" },
                    { "verbosityPack", "Detailed" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            netCorePkgPush.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "NuGet Push",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "push" },
                    { "publishWebProjects", "true" },
                    { "projects", "" },
                    { "custom", "" },
                    { "arguments", "" },
                    { "publishTestResults", "true" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "select" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "ee376baf-a79d-462e-9f9d-0b1623d4de07" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "off" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "" },
                    { "verbosityPack", "Detailed" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region .Net Core Build, Test
            var netCoreBldTst = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - .Net Core Build, Test",
                Description = "Restore|Build|Test",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - .Net Core Build, Test",
                InstanceNameFormat = "Low Code Unit - .Net Core Build, Test | $(BuildConfiguration)"
            };

            netCoreBldTst.RunsOn.Clear();

            netCoreBldTst.RunsOn.Add("Agent");

            netCoreBldTst.Inputs.Add(bldCfgInput);

            netCoreBldTst.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Restore",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "restore" },
                    { "publishWebProjects", "true" },
                    { "projects", "**/*.csproj " },
                    { "custom", "" },
                    { "arguments", "--configfile .nuget/NuGet.config" },
                    { "publishTestResults", "true" },
                    { "testRunTitle", "" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "config" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", ".nuget/NuGet.config" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "off" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "" },
                    { "verbosityPack", "Detailed" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            netCoreBldTst.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Build",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "build" },
                    { "publishWebProjects", "true" },
                    { "projects", "**/*.csproj" },
                    { "custom", "" },
                    { "arguments", @"--configuration $(BuildConfiguration) /p:FileVersion=$(Build.BuildNumber) /p:AssemblyVersion=$(Build.BuildNumber) /p:PackageVersion=$(Build.BuildNumber) /p:DeployOnBuild=true /p:DeployDefaultTarget=WebPublish /p:WebPublishMethod=FileSystem /p:publishUrl=""$(Agent.TempDirectory)\WebAppContent\\""" },
                    { "publishTestResults", "true" },
                    { "testRunTitle", "" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "select" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "off" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "" },
                    { "verbosityPack", "Detailed" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            netCoreBldTst.Tasks.Add(new TaskGroupStep()
            {
                Condition = "eq(variables['RunTests'], true)",
                DisplayName = "Test",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "test" },
                    { "publishWebProjects", "true" },
                    { "projects", "**/*Tests/*.csproj" },
                    { "custom", "" },
                    { "arguments", "--configuration $(BuildConfiguration)" },
                    { "publishTestResults", "true" },
                    { "testRunTitle", "" },
                    { "zipAfterPublish", "true" },
                    { "modifyOutputPath", "true" },
                    { "selectOrConfig", "select" },
                    { "feedRestore", "" },
                    { "includeNuGetOrg", "true" },
                    { "nugetConfigPath", "" },
                    { "externalEndpoints", "" },
                    { "noCache", "false" },
                    { "packagesDirectory", "" },
                    { "verbosityRestore", "Detailed" },
                    { "searchPatternPush", "$(Build.ArtifactStagingDirectory)/*.nupkg" },
                    { "nuGetFeedType", "internal" },
                    { "feedPublish", "" },
                    { "publishPackageMetadata", "true" },
                    { "externalEndpoint", "" },
                    { "searchPatternPack", "**/*.csproj" },
                    { "configurationToPack", "$(BuildConfiguration)" },
                    { "outputDir", "$(Build.ArtifactStagingDirectory)" },
                    { "nobuild", "false" },
                    { "versioningScheme", "off" },
                    { "versionEnvVar", "" },
                    { "requestedMajorVersion", "1" },
                    { "requestedMinorVersion", "0" },
                    { "requestedPatchVersion", "0" },
                    { "buildProperties", "" },
                    { "verbosityPack", "Detailed" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5541a522-603c-47ad-91fc-a4b1d163081b"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region .Net Core Build, Test, Package
            var netCoreBldTstPkg = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - .Net Core Build, Test, Package",
                Description = "Restore|Build|Test|Package|Push",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - .Net Core Build, Test, Package",
                InstanceNameFormat = "Low Code Unit - .Net Core Build, Test, Package | $(BuildConfiguration)"
            };

            netCoreBldTstPkg.RunsOn.Clear();

            netCoreBldTstPkg.RunsOn.Add("Agent");

            netCoreBldTstPkg.Inputs.Add(bldCfgInput);

            netCoreBldTstPkg.Tasks.Add(netCoreBldTst.Tasks.ElementAt(0));

            netCoreBldTstPkg.Tasks.Add(netCoreBldTst.Tasks.ElementAt(1));

            netCoreBldTstPkg.Tasks.Add(netCoreBldTst.Tasks.ElementAt(2));

            netCoreBldTstPkg.Tasks.Add(netCorePkgPush.Tasks.ElementAt(0));

            netCoreBldTstPkg.Tasks.Add(netCorePkgPush.Tasks.ElementAt(1));

            netCoreBldTstPkg.Tasks.Add(netCorePkgPush.Tasks.ElementAt(2));
            #endregion

            #region NPM Build
            var npmBld = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - NPM Build",
                Description = "Injects current version before running npm install and then the npm deploy commands inside of the projects package.json file",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - NPM Build",
                InstanceNameFormat = "Low Code Unit - NPM Build | $(BuildConfiguration)"
            };

            npmBld.RunsOn.Clear();

            npmBld.RunsOn.Add("Agent");

            npmBld.RunsOn.Add("DeploymentGroup");

            var rplVerPreScript = new StringBuilder();
            rplVerPreScript.AppendLine("(Get-Content -path package.json -Raw) -replace \"version patch\",\"version $(Build.BuildNumber)-$(Build.SourceBranchName) --no-git-tag-version -f\"");
            rplVerPreScript.AppendLine("(Get-Content -path package.json -Raw) -replace \"version patch\",\"version $(Build.BuildNumber)-$(Build.SourceBranchName) --no-git-tag-version -f\" | Set-Content -Path package.json");
            rplVerPreScript.AppendLine("Write-Host Successfully replaced version in package.json");

            npmBld.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), ne(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Replace Version in package.json (Prerelease)",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "targetType", "inline" },
                    { "filePath", "" },
                    { "arguments", "" },
                    { "script", rplVerPreScript.ToString() },
                    { "errorActionPreference", "stop" },
                    { "failOnStderr", "false" },
                    { "ignoreLASTEXITCODE", "false" },
                    { "pwsh", "false" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("e213ff0f-5d5c-4791-802d-52ea3e7be1f1"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            var rplVerProScript = new StringBuilder();
            rplVerProScript.AppendLine("(Get-Content -path package.json -Raw) -replace \"version patch\",\"version $(Build.BuildNumber) --no-git-tag-version -f\"");
            rplVerProScript.AppendLine("(Get-Content -path package.json -Raw) -replace \"version patch\",\"version $(Build.BuildNumber) --no-git-tag-version -f\" | Set-Content -Path package.json");
            rplVerProScript.AppendLine("Write-Host Successfully replaced version in package.json");

            npmBld.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Replace Version in package.json (Production)",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "targetType", "inline" },
                    { "filePath", "" },
                    { "arguments", "" },
                    { "script", rplVerProScript.ToString() },
                    { "errorActionPreference", "stop" },
                    { "failOnStderr", "false" },
                    { "ignoreLASTEXITCODE", "false" },
                    { "pwsh", "false" },
                    { "workingDirectory", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("e213ff0f-5d5c-4791-802d-52ea3e7be1f1"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            npmBld.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "NPM Install",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "install" },
                    { "workingDir", "" },
                    { "verbose", "false" },
                    { "customCommand", "" },
                    { "customRegistry", "useNpmrc" },
                    { "customFeed", "" },
                    { "customEndpoint", endpoints["npmrc"] },
                    { "publishRegistry", "useExternalRegistry" },
                    { "publishFeed", "" },
                    { "publishEndpoint", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("fe47e961-9fa8-4106-8639-368c022d43ad"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });

            npmBld.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "NPM Deploy",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "command", "custom" },
                    { "workingDir", "" },
                    { "verbose", "false" },
                    { "customCommand", "run deploy" },
                    { "customRegistry", "useNpmrc" },
                    { "customFeed", "" },
                    { "customEndpoint", endpoints["npmrc"] },
                    { "publishRegistry", "useExternalRegistry" },
                    { "publishFeed", "" },
                    { "publishEndpoint", "" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("fe47e961-9fa8-4106-8639-368c022d43ad"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region Copy & Publish Artifacts
            var cpyPub = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - Copy & Publish Artifacts",
                Description = "Copy|Publish",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - Copy & Publish Artifacts",
                InstanceNameFormat = "Low Code Unit - Copy & Publish Artifacts | $(BuildConfiguration)"
            };

            cpyPub.RunsOn.Clear();

            cpyPub.RunsOn.Add("Agent");

            cpyPub.RunsOn.Add("DeploymentGroup");

            cpyPub.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Copy Artifact",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "SourceFolder", "$(build.sourcesdirectory)" },
                    { "Contents", @"**\PublishProfiles\*.xml **\ApplicationParameters\*.xml" },
                    { "TargetFolder", "$(build.artifactstagingdirectory)\fabricartifacts" },
                    { "CleanTargetFolder", "false" },
                    { "OverWrite", "false" },
                    { "flattenFolders", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("5bfb729a-a7c8-4a78-a7c3-8d717bb7c13c"),
                    VersionSpec = "2.*",
                    DefinitionType = "task"
                }
            });

            cpyPub.Tasks.Add(new TaskGroupStep()
            {
                Condition = "succeeded()",
                DisplayName = "Publish Artifact",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "PathtoPublish", "$(build.artifactstagingdirectory)" },
                    { "ArtifactName", "drop" },
                    { "ArtifactType", "Container" },
                    { "TargetPath", @"\\my\share\$(Build.DefinitionName)\$(Build.BuildNumber)" },
                    { "Parallel", "false" },
                    { "ParallelCount", "8" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("2ff763a7-ce83-4e1f-bc89-0ae63477cebe"),
                    VersionSpec = "1.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            #region Git Label
            var gitLbl = new TaskGroupCreateParameter()
            {
                Author = "LowCodeUnit",
                Category = "Build",
                Name = "Low Code Unit - Git Label",
                Description = "Git Label",
                IconUrl = "/_static/tfs/M140_20181015.3/_content/icon-meta-task.png",
                FriendlyName = "Low Code Unit - Git Label",
                InstanceNameFormat = "Low Code Unit - Git Label | $(BuildConfiguration)"
            };

            gitLbl.RunsOn.Clear();

            gitLbl.RunsOn.Add("Agent");

            gitLbl.RunsOn.Add("DeploymentGroup");

            gitLbl.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), ne(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Git Label",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "workingdir", "$(build.SourcesDirectory)" },
                    { "tagUser", "DevOps" },
                    { "tagEmail", "devops@fathym.com" },
                    { "tag", "$(build.buildNumber)" },
                    { "tagMessage", "$(build.buildNumber)" },
                    { "useLightweightTags", "false" },
                    { "forceTagCreation", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("be6aa0ca-e62f-414d-aaa6-e9524b556482"),
                    VersionSpec = "4.*",
                    DefinitionType = "task"
                }
            });

            gitLbl.Tasks.Add(new TaskGroupStep()
            {
                Condition = "and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/master'))",
                DisplayName = "Git Label (Production)",
                Enabled = true,
                Inputs = new Dictionary<string, string>()
                {
                    { "workingdir", "$(build.SourcesDirectory)" },
                    { "tagUser", "DevOps" },
                    { "tagEmail", "devops@fathym.com" },
                    { "tag", "Production-$(build.buildNumber)" },
                    { "tagMessage", "$(build.buildNumber)" },
                    { "useLightweightTags", "false" },
                    { "forceTagCreation", "false" }
                },
                Task = new Microsoft.TeamFoundation.DistributedTask.WebApi.TaskDefinitionReference()
                {
                    Id = new Guid("be6aa0ca-e62f-414d-aaa6-e9524b556482"),
                    VersionSpec = "4.*",
                    DefinitionType = "task"
                }
            });
            #endregion

            return new List<TaskGroupCreateParameter>()
            {
                azFuncTask, nugRstr, netCoreBldTst, netCoreBldTstPkg, netCorePkgPush, npmBld, cpyPub, gitLbl
            };
        }

        protected virtual async Task<LibGit2Sharp.Signature> loadGitHubSignature()
        {
            var usersClient = new Octokit.UsersClient(new Octokit.ApiConnection(gitHubClient.Connection));

            var user = await usersClient.Current();

            var userEmails = await usersClient.Email.GetAll();

            var userEmail = userEmails.FirstOrDefault(ue => ue.Primary) ?? userEmails.FirstOrDefault();

            return new LibGit2Sharp.Signature(user.Login, userEmail.Email, DateTimeOffset.Now);
        }

        protected virtual async Task seedRepo(string filesRoot, TeamProjectReference project, BuildDefinitionReference buildDef, InfrastructureApplicationSeedOption appSeed,
            string repoOrg, string repoName)
        {
            var appRepo = await getOrCreateRepository(repoOrg, repoName);

            log.LogInformation($"Repository {appRepo?.Name}");

            var repoPath = Path.Combine(filesRoot, $"git\\repos\\{repoOrg}\\{repoName}");

            var repoDir = new DirectoryInfo(repoPath);

            await ensureRepo(repoDir, appRepo.CloneUrl);

            var repoFiles = repoDir.GetFiles();

            if (!repoFiles.Any(rf => rf.Name == "lcu.json"))
            {
                log.LogInformation($"Repository {repoDir.FullName} ensured");

                var lineCount = 0;

                var npm = new System.Diagnostics.Process();

                npm.StartInfo.CreateNoWindow = true;

                log.LogInformation($"Command EXE: {cmdExePath}");

                log.LogInformation($"Command PATH variable: {cmdPathVariable}");

                if (!cmdPathVariable.IsNullOrEmpty())
                    npm.StartInfo.Environment["Path"] = cmdPathVariable;

                npm.StartInfo.FileName = cmdExePath;

                npm.StartInfo.RedirectStandardInput = true;

                npm.StartInfo.RedirectStandardError = true;

                npm.StartInfo.RedirectStandardOutput = true;

                npm.StartInfo.UseShellExecute = false;

                npm.StartInfo.WorkingDirectory = repoPath;

                npm.EnableRaisingEvents = true;

                npm.ErrorDataReceived += (sender, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                    {
                        lineCount++;

                        log.LogInformation($"[{lineCount}]: {e.Data}");
                    }
                };

                npm.OutputDataReceived += (sender, e) =>
                {
                    // Prepend line numbers to each line of the output.
                    if (!String.IsNullOrEmpty(e.Data))
                    {
                        lineCount++;

                        log.LogInformation($"[{lineCount}]: {e.Data}");
                    }
                };

                log.LogInformation($"Executing commands...");

                appSeed.Commands.ForEach(command =>
                {
                    lineCount = 0;

                    npm.Start();

                    npm.BeginOutputReadLine();

                    npm.BeginErrorReadLine();

                    var runCmd = command
                        .Replace("{{repoName}}", repoName)
                        .Replace("{{repoOrg}}", repoOrg)
                        .Replace("{{projName}}", repoName);

                    log.LogInformation($"Executing command {runCmd}");

                    npm.StandardInput.WriteLine($"{runCmd} & exit");

                    npm.WaitForExit();

                    npm.CancelErrorRead();

                    npm.CancelOutputRead();
                });

                log.LogInformation($"Commands executed");

                var credsProvider = loadCredHandler();

                await commitAndSync($"Seeding with {state.AppSeed.SelectedSeed}", repoPath, credsProvider);

                await startBuildAndWait(project, buildDef);
            }
        }

        protected virtual async Task<Status> startBuildAndWait(TeamProjectReference project, DefinitionReference buildDef)
        {
            var build = await bldClient.QueueBuildAsync(new Build()
            {
                Project = project,
                Priority = QueuePriority.High,
                Definition = buildDef
            });

            var status = Status.GeneralError;

            do
            {
                build = await bldClient.GetBuildAsync(project.Id.ToString(), build.Id);

                status = build.Status.HasValue && build.Status.Value == BuildStatus.Completed;

                await Task.Delay(1000);
            } while (!status);

            return status;
        }

        protected virtual async Task<Octokit.Repository> tryGetRepository(string repoOrg, string repoName)
        {
            Octokit.Repository repo = null;

            try
            {
                repo = await gitHubClient.Repository.Get(repoOrg, repoName);
            }
            catch (Octokit.NotFoundException nfex)
            { }

            return repo;
        }
        #endregion
    }

    public class PrvGraph : ProvisioningGraph
    {
        #region Properties

        #endregion

        #region Constructors
        public PrvGraph(LCUGraphConfig config)
            : base(config)
        { }
        #endregion

        #region API Methods
        public virtual async Task<MetadataModel> GetEnvironmentSettings(string apiKey, string envLookup)
        {
            return await withG(async (client, g) =>
            {
                var query = g.V().HasLabel(EntGraphConstants.EnvironmentVertexName)
                    .Has("Registry", apiKey)
                    .Has("EnterprisePrimaryAPIKey", apiKey)
                    .Has("Lookup", envLookup)
                    .Out(EntGraphConstants.ConsumesEdgeName)
                    .HasLabel(EntGraphConstants.EnvironmentVertexName + "Settings")
                    .Has("Registry", apiKey)
                    .Has("EnterprisePrimaryAPIKey", apiKey);

                var results = await Submit<MetadataModel>(query);

                return results.FirstOrDefault();
            });
        }

        public virtual async Task<MetadataModel> SaveEnvironmentSettings(string apiKey, string envLookup, MetadataModel settings)
        {
            return await withG(async (client, g) =>
            {
                var existingQuery = g.V().HasLabel(EntGraphConstants.EnvironmentVertexName)
                    .Has("Registry", apiKey)
                    .Has("EnterprisePrimaryAPIKey", apiKey)
                    .Has("Lookup", envLookup)
                    .HasLabel(EntGraphConstants.EnvironmentVertexName + "Settings")
                    .Has("Registry", apiKey)
                    .Has("EnterprisePrimaryAPIKey", apiKey);

                var existingEnvSetResults = await Submit<BusinessModel<Guid>>(existingQuery);

                var existingEnvSetResult = existingEnvSetResults.FirstOrDefault();

                var query = existingEnvSetResult == null ?
                    g.AddV(EntGraphConstants.EnvironmentVertexName + "Settings")
                    .Property("EnterprisePrimaryAPIKey", apiKey)
                    .Property("Registry", apiKey) : existingQuery;

                settings.Metadata.Each(md =>
                {
                    query = query.Property(md.Key, md.Value?.ToString() ?? "");
                });

                var envSetResults = await Submit<BusinessModel<Guid>>(query);

                var envSetResult = envSetResults.FirstOrDefault();

                var envQuery = g.V().HasLabel(EntGraphConstants.EnvironmentVertexName)
                    .Has("Registry", apiKey)
                    .Has("EnterprisePrimaryAPIKey", apiKey)
                    .Has("Lookup", envLookup);

                var envResults = await Submit<Graphs.Registry.Enterprises.Provisioning.Environment>(envQuery);

                var envResult = envResults.FirstOrDefault();

                var edgeResults = await Submit<BusinessModel<Guid>>(g.V(envResult.ID).Out(EntGraphConstants.OwnsEdgeName).HasId(envSetResult.ID));

                var edgeResult = edgeResults.FirstOrDefault();

                if (edgeResult == null)
                {
                    var edgeQueries = new[] {
                        g.V(envResult.ID).AddE(EntGraphConstants.ConsumesEdgeName).To(g.V(envSetResult.ID)),
                        g.V(envResult.ID).AddE(EntGraphConstants.OwnsEdgeName).To(g.V(envSetResult.ID)),
                        g.V(envResult.ID).AddE(EntGraphConstants.ManagesEdgeName).To(g.V(envSetResult.ID))
                    };

                    foreach (var edgeQuery in edgeQueries)
                        await Submit(edgeQuery);
                }

                return envSetResult;
            });
        }
        #endregion

        #region Helpers
        #endregion
    }

    public class IDGraph : IdentityGraph
    {
        #region Properties

        #endregion

        #region Constructors
        public IDGraph(LCUGraphConfig config)
            : base(config)
        { }
        #endregion

        #region API Methods
        #endregion

        #region Helpers
        #endregion
    }
}
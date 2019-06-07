using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Fathym;
using Fathym.Business.Models;
using Fathym.Design.Singleton;
using LCU.Graphs;
using LCU.Graphs.Registry.Enterprises;
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
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using System.Threading;

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
        protected readonly string devOpsToken;

        protected readonly VssConnection devOpsConn;

        protected readonly Octokit.GitHubClient gitHubClient;

        protected readonly string gitHubToken;

        protected readonly IDGraph idGraph;

        protected readonly PrvGraph prvGraph;
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        {
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
            }

            gitHubToken = idGraph.RetrieveThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "GIT-HUB").Result;

            if (!gitHubToken.IsNullOrEmpty())
            {
                gitHubClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("LCU-STATE-API-FORGE-INFRASTRUCTURE"));

                var tokenAuth = new Octokit.Credentials(gitHubToken);

                gitHubClient.Credentials = tokenAuth;
            }
        }
        #endregion

        #region API Methods

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

        public virtual async Task<ForgeInfrastructureState> ConfigureDevOps()
        {
            var repoName = state.EnvSettings?.Metadata?["GitHubRepository"]?.ToString();

            var repoOrg = state.EnvSettings?.Metadata?["GitHubOrganization"]?.ToString();

            TeamProjectReference project = null;

            using (var projClient = devOpsConn.GetClient<ProjectHttpClient>())
            {
                var projects = await projClient.GetProjects(ProjectState.All);

                project = projects.FirstOrDefault(p => p.Name == "LCU OS");

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
            }

            var name = $"{repoOrg} {repoName}";

            var repo = await gitHubClient.Repository.Get(repoOrg, repoName);

            var gitHubCSId = "";

            var azureRMCSId = "";

            var azure = Azure.Authenticate(getAuthorization());

            var azureSub = azure.Subscriptions.GetById(state.EnvSettings.Metadata["AzureSubID"].ToString());

            using (var seClient = devOpsConn.GetClient<ServiceEndpointHttpClient>())
            {
                var ses = await seClient.GetServiceEndpointsAsync(project.Id.ToString());

                gitHubCSId = ses?.FirstOrDefault(se => se.Type.ToLower() == "github")?.Id.ToString();

                azureRMCSId = ses?.FirstOrDefault(se => se.Type.ToLower() == "azurerm" && se.Name == $"Azure {azureSub.DisplayName}")?.Id.ToString();

                if (azureRMCSId.IsNullOrEmpty())
                {
                    var se = await seClient.CreateServiceEndpointAsync(project.Id, new ServiceEndpoint()
                    {
                        Authorization = new EndpointAuthorization()
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
                            { "subscriptionName", azureSub.DisplayName },
                            { "subscriptionId", state.EnvSettings.Metadata["AzureSubID"].ToString() }
                        },
                        Name = $"Azure {azureSub.DisplayName}",
                        Type = "azurerm",
                        Url = new Uri("https://management.azure.com/")
                    });

                    azureRMCSId = se?.Id.ToString();
                }
            }

            var process = new DesignerProcess();

            var phase = new Phase()
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
                Steps = new List<BuildDefinitionStep>()
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
                            TaskDefinition = new TaskDefinitionReference()
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
                            TaskDefinition = new TaskDefinitionReference()
                            {
                                DefinitionType= "task",
                                VersionSpec = "1.*",
                                Id = new Guid("2ff763a7-ce83-4e1f-bc89-0ae63477cebe")
                            }
                        }
                    }
            };

            process.Phases.Add(phase);

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
                    Pool = new TaskAgentPoolReference()
                    {
                        Name = "Hosted VS2017",
                        IsHosted = true
                    }
                },
                Repository = new BuildRepository()
                {
                    Id = $"{repoOrg}/{repoName}",
                    DefaultBranch = "master",
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

            buildDef.Repository.Properties.Add("defaultBranch", "master");

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

            buildDef.Triggers.Add(ci);

            buildDef.Variables.Add("MajorVersion", new BuildDefinitionVariable()
            {
                Value = "0"
            });

            buildDef.Variables.Add("MinorVersion", new BuildDefinitionVariable()
            {
                Value = "1"
            });

            using (var bldClient = devOpsConn.GetClient<BuildHttpClient>())
            {
                buildDef = await bldClient.CreateDefinitionAsync(buildDef);
            }

            using (var bldClient = devOpsConn.GetClient<ReleaseHttpClient>())
            {
                var artifact = new Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts.Artifact()
                {
                    Alias = $"_{repoOrg} {repoName}",
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

                var safeEnvName = state.Environment.Lookup.Replace("-", String.Empty);

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
                            // Conditions = new List<Condition>()
                            // {
                            //     new Condition()
                            //     {
                            //         ConditionType= ConditionType.Event,
                            //         Name = "ReleaseStarted",
                            //         Value = String.Empty
                            //     }
                            // },
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
                                    WorkflowTasks = new List<WorkflowTask>()
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
                                    }
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

                var release = await bldClient.CreateReleaseDefinitionAsync(releaseDef, project.Id);
            }

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

        public virtual async Task<ForgeInfrastructureState> CreateGitHubRepository(string org, string repoName)
        {
            var newRepo = new Octokit.NewRepository(repoName)
            {

            };

            var res = await gitHubClient.Repository.Create(newRepo);

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            if (state.DevOps == null)
                state.DevOps = new DevOpsState();

            if (state.GitHub == null || gitHubClient == null)
                state.GitHub = new GitHubState();

            if (state.InfraTemplate == null || gitHubClient == null)
                state.InfraTemplate = new InfrastructureTemplateState();

            return await WhenAll(
                HasDevOps(),
                HasDevOpsSetup(),
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
                using (var projClient = devOpsConn.GetClient<ProjectHttpClient>())
                {
                    var projects = await projClient.GetProjects(ProjectState.All);

                    project = projects.FirstOrDefault(p => p.Name == "LCU OS");
                }
            }

            if (project != null)
            {
                using (var bldClient = devOpsConn.GetClient<ReleaseHttpClient>())
                {
                    var safeEnvName = state.Environment.Lookup.Replace("-", String.Empty);

                    var releases = await bldClient.GetReleaseDefinitionsAsync(project.Id);

                    var name = $"{repoOrg} {repoName}";

                    var release = releases.FirstOrDefault(r => r.Name == name);

                    state.DevOps.Setup = release != null;
                }
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
        }

        protected virtual AzureCredentials getAuthorization()
        {
            return new AzureCredentials(new ServicePrincipalLoginInformation()
            {
                ClientId = state.EnvSettings.Metadata["AzureAppID"].ToString(),
                ClientSecret = state.EnvSettings.Metadata["AzureAppAuthKey"].ToString()
            }, state.EnvSettings.Metadata["AzureTenantID"].ToString(), AzureEnvironment.AzureGlobalCloud);
        }

        protected virtual LibGit2Sharp.Handlers.CredentialsHandler loadCredHandler()
        {
            return (LibGit2Sharp.Handlers.CredentialsHandler)((url, user, cred) =>
            {
                return new LibGit2Sharp.UsernamePasswordCredentials { Username = gitHubToken, Password = "" };
            });
        }

        protected virtual async Task<LibGit2Sharp.Signature> loadGitHubSignature()
        {
            var usersClient = new Octokit.UsersClient(new Octokit.ApiConnection(gitHubClient.Connection));

            var user = await usersClient.Current();

            var userEmails = await usersClient.Email.GetAll();

            var userEmail = userEmails.FirstOrDefault(ue => ue.Primary) ?? userEmails.FirstOrDefault();

            return new LibGit2Sharp.Signature(user.Login, userEmail.Email, DateTimeOffset.Now);
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
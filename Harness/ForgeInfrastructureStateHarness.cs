using System;
using System.Globalization;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Fathym;
using Fathym.API;
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
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Fathym.Design;
using LCU.Presentation.Personas.Applications;
using LCU.Presentation.Personas.DevOps;
using LCU.Presentation.Personas.Security;

namespace LCU.State.API.Forge.Infrastructure.Harness
{
    public class EnterpriseManagerClient : LCU.Presentation.Personas.Enterprises.EnterpriseManagerClient
    {
        #region Constructors
        public EnterpriseManagerClient(string apiRoot)
            : base(apiRoot)
        { }
        #endregion

        #region API Methods
        public override async Task<T> Get<T>(string requestUri)
        {
            var respStr = await web.GetStringAsync(requestUri);

            return respStr?.FromJSON<T>();
        }

        public new virtual async Task<BaseResponse<List<MetadataModel>>> ListGitHubOrganizations(string entApiKey, string username)
        {
            var response = await Get<BaseResponse<List<MetadataModel>>>($"source-control/{entApiKey}/git-hub-orgs?username={username}");

            return response;
        }

        #endregion
    }

    public class ForgeInfrastructureStateHarness : LCUStateHarness<ForgeInfrastructureState>
    {
        #region Fields
        protected readonly DevOpsArchitectClient devOpsArch;

        protected readonly EnterpriseManagerClient entMgr;

        protected readonly SecurityManagerClient secMgr;
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        {
            devOpsArch = req.ResolveClient<DevOpsArchitectClient>();

            entMgr = req.ResolveClient<EnterpriseManagerClient>();

            secMgr = req.ResolveClient<SecurityManagerClient>();
        }
        #endregion

        #region API Methods
        public virtual async Task<ForgeInfrastructureState> AppSeedCompleteCheck(string filesRoot)
        {
            var appSeed = state.AppSeed.Options.FirstOrDefault(o => o.Lookup == state.AppSeed.SelectedSeed);

            var completed = await appDev.AppSeedCompleteCheck(new Presentation.Personas.Applications.AppSeedCompleteCheckRequest()
            {
                AppSeed = appSeed,
                RepoName = state.AppSeed.NewName
            }, details.EnterpriseAPIKey, state.Environment.Lookup, details.Host, details.Username);

            state.AppSeed.InfraBuilt = completed.Model.InfraBuilt;

            state.AppSeed.AppSeeded = completed.Model.AppSeeded;

            state.AppSeed.HasBuild = completed.Model.HasBuild;

            state.AppSeed.AppSeedBuilt = completed.Model.AppSeedBuilt;

            state.AppSeed.Step = completed.Model.AppSeedLCU ? ForgeInfrastructureApplicationSeedStepTypes.Created : state.AppSeed.Step;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> CommitInfrastructure(string selectedTemplate)
        {
            if (state.InfraTemplate.SelectedTemplate.IsNullOrEmpty())
                state.InfraTemplate.SelectedTemplate = selectedTemplate;

            var committed = await devOpsArch.CommitInfrastructure(new Presentation.Personas.DevOps.CommitInfrastructureRequest()
            {
                EnvironmentLookup = state.Environment.Lookup,
                SelectedTemplate = state.InfraTemplate.SelectedTemplate,
            }, details.EnterpriseAPIKey, state.Environment.Lookup, details.Username);

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ConfigureDevOps(string npmRegistry, string npmAccessToken)
        {
            var configured = await devOpsArch.ConfigureDevOps(new Presentation.Personas.DevOps.ConfigureDevOpsRequest()
            {
                NPMAccessToken = npmAccessToken,
                NPMRegistry = npmRegistry
            }, details.EnterpriseAPIKey, state.Environment.Lookup, details.Username);

            state.DevOps.NPMRegistry = npmRegistry;

            state.DevOps.NPMAccessToken = npmAccessToken;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ConfigureInfrastructure(string infraType, bool useDefaultSettings, MetadataModel settings)
        {
            var envLookup = $"{state.GitHub.SelectedOrg}-prd";

            var configured = await devOpsArch.ConfigureInfrastructure(new Presentation.Personas.DevOps.ConfigureInfrastructureRequest()
            {
                EnvSettings = settings,
                OrganizationLookup = state.GitHub.SelectedOrg,
                InfraType = infraType,
                UseDefaultSettings = useDefaultSettings
            }, details.EnterpriseAPIKey, envLookup, details.Username);

            if (configured.Status)
            {
                state.Environment = configured.Model;

                var envSettings = await entMgr.GetEnvironmentSettings(details.EnterpriseAPIKey, state.Environment.Lookup);

                state.EnvSettings = envSettings.Model;
            }
            else
                state.Error = configured.Status.Message;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> CreateAppFromSeed(string filesRoot, string name)
        {
            var appSeed = state.AppSeed.Options.FirstOrDefault(o => o.Lookup == state.AppSeed.SelectedSeed);

            var repoName = state.AppSeed.NewName = appSeed?.SeedFork?.Repository.ToLower() ?? name.ToLower();

            var created = await appDev.CreateAppFromSeed(new Presentation.Personas.Applications.CreateAppFromSeedRequest()
            {
                AppSeed = appSeed,
                RepoName = repoName
            }, details.EnterpriseAPIKey, state.Environment.Lookup, details.Username);

            state.AppSeed.Step = ForgeInfrastructureApplicationSeedStepTypes.Creating;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            if (state.AppSeed == null)
                state.AppSeed = new InfrastructureApplicationSeedState()
                {
                    Options = new List<InfrastructureApplicationSeedOption>()
                };

            await GetEnvironments();

            await HasDevOpsSetup();

            if (state.DevOps == null || !state.DevOps.Configured)
                state.DevOps = new DevOpsState();

            var tpd = await secMgr.RetrieveIdentityThirdPartyData(details.EnterpriseAPIKey, details.Username, "NPM-RC-TOKEN", "NPM-RC-REGISTRY");

            state.DevOps.NPMRegistry = tpd.Model["NPM-RC-REGISTRY"] ?? "https://registry.npmjs.org/";

            state.DevOps.NPMAccessToken = tpd.Model["NPM-RC-TOKEN"];

            var hasSourceControl = await entMgr.HasSourceControlOAuth(details.EnterpriseAPIKey, details.Username);

            if (state.GitHub == null || !hasSourceControl.Status)
                state.GitHub = new GitHubState();

            if (state.InfraTemplate == null || !hasSourceControl.Status)
                state.InfraTemplate = new InfrastructureTemplateState();

            return await WhenAll(
                HasDevOps(),
                GetEnterprise(),
                HasInfrastructure(),
                HasSourceControl(),
                ListGitHubOrganizations(),
                ListGitHubOrgRepos()
            );
        }

        public virtual async Task<ForgeInfrastructureState> GetEnterprise()
        {
            var ent = await entMgr.GetEnterprise(details.EnterpriseAPIKey);

            state.EnterpriseName = ent.Model?.Name;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> GetEnvironments()
        {
            var envs = await entMgr.ListEnvironments(details.EnterpriseAPIKey);

            if (!envs.Model.IsNullOrEmpty())
            {
                state.Environment = envs.Model.FirstOrDefault();

                var envSettings = await entMgr.GetEnvironmentSettings(details.EnterpriseAPIKey, state.Environment?.Lookup);

                state.EnvSettings = envSettings.Model;
            }
            else
            {
                state.Environment = null;

                state.EnvSettings = null;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasDevOps()
        {
            var hasDevOps = await entMgr.HasDevOpsOAuth(details.EnterpriseAPIKey, details.Username);

            state.DevOps.Configured = hasDevOps.Status;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasDevOpsSetup()
        {
            if (state.Environment != null)
            {
                var isDevOpsSetup = await entMgr.IsDevOpsSetup(details.EnterpriseAPIKey, state.Environment.Lookup, details.Username);

                state.DevOps.Setup = isDevOpsSetup.Status;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasInfrastructure()
        {
            var envs = await entMgr.ListEnvironments(details.EnterpriseAPIKey);

            state.InfrastructureConfigured = !envs.Model.IsNullOrEmpty() && envs.Model.Any(env =>
            {
                return env != null;
            });

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasProdConfig()
        {
            if (state.Environment != null)
            {
                var hasProdEnv = await entMgr.HasProductionEnvironment(details.EnterpriseAPIKey, state.Environment?.Lookup, details.Username);

                state.ProductionConfigured = hasProdEnv.Status;
            }
            else
                state.ProductionConfigured = false;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasSourceControl()
        {
            var hasSC = await entMgr.HasSourceControlOAuth(details.EnterpriseAPIKey, details.Username);

            state.SourceControlConfigured = hasSC.Status;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ListGitHubOrganizations()
        {
            var gitHubOrgs = await entMgr.ListGitHubOrganizations(details.EnterpriseAPIKey, details.Username);

            state.GitHub.Organizations = gitHubOrgs.Model;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> ListGitHubOrgRepos()
        {
            if (!state.GitHub.SelectedOrg.IsNullOrEmpty())
            {
                var gitHubOrgRepos = await entMgr.ListGitHubOrganizationRepos(details.EnterpriseAPIKey, state.GitHub.SelectedOrg, details.Username);

                state.GitHub.OrgRepos = gitHubOrgRepos.Model;
            }

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> LoadInfrastructureRepository()
        {
            if (state.Environment != null)
            {
                var infaConfig = await entMgr.LoadInfrastructureConfig(details.EnterpriseAPIKey, state.Environment?.Lookup, details.Username);

                state.InfraTemplate.Options = infaConfig.Model.InfraTemplateOptions;

                state.AppSeed.Options = infaConfig.Model.AppSeedOptions;
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

        public virtual async Task<ForgeInfrastructureState> SetupDevOpsOAuth(string devOpsAppId, string devOpsClientSecret, string devOpsScopes)
        {
            var devOpsOAuth = await entMgr.SetupDevOpsOAuthConnection(new Presentation.Personas.Enterprises.SetupDevOpsOAuthConnectionRequest()
            {
                DevOpsAppID = devOpsAppId,
                DevOpsClientSecret = devOpsClientSecret,
                DevOpsScopes = devOpsScopes
            }, details.EnterpriseAPIKey);

            state.DevOps.OAuthConfigured = devOpsOAuth.Status;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetupGitHubOAuth(string gitHubClientId, string gitHubClientSecret)
        {
            var gitHubOAuth = await entMgr.SetupGitHubOAuthConnection(new Presentation.Personas.Enterprises.SetupGitHubOAuthConnectionRequest()
            {
                GitHubClientID = gitHubClientId,
                GitHubClientSecret = gitHubClientSecret
            }, details.EnterpriseAPIKey);

            state.GitHub.OAuthConfigured = gitHubOAuth.Status;

            return state;
        }
        #endregion

        #region Helpers
        #endregion
    }
}
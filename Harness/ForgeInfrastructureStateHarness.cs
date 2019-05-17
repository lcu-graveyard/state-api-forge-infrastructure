using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Fathym;
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

namespace LCU.State.API.Forge.Infrastructure.Harness
{
    public class ForgeInfrastructureStateHarness : LCUStateHarness<ForgeInfrastructureState>
    {
        #region Fields
        protected readonly IdentityGraph idGraph;

        protected readonly ProvisioningGraph prvGraph;
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        {
            idGraph = req.LoadGraph<IdentityGraph>(log);

            prvGraph = req.LoadGraph<ProvisioningGraph>(log);
        }
        #endregion

        #region API Methods
        public virtual async Task<ForgeInfrastructureState> ConfigureInfrastructure(string infraType, bool useDefaultSettings, MetadataModel settings)
        {
            if (useDefaultSettings && infraType == "Azure")
            {
                var env = await prvGraph.SaveEnvironment(new Graphs.Registry.Enterprises.Provisioning.Environment() {
                    EnterprisePrimaryAPIKey = details.EnterpriseAPIKey,
                    Lookup = "prod",
                    Name = $"Production"
                });
                
                await HasInfrastructure();

                state.SetupStep = null;
            }
            else
                state.Error = "Only Azure Default Settings are currently supported";

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            state.SetupStep = null;

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasInfrastructure()
        {
            var gitHubToken = await idGraph.RetrieveThirdPartyAccessToken(details.EnterpriseAPIKey, details.Username, "GIT-HUB");

            state.SourceControlConfigured = !gitHubToken.IsNullOrEmpty();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> HasSourceControl()
        {
            var envs = await prvGraph.ListEnvironments(details.EnterpriseAPIKey);

            state.InfrastructureConfigured = !envs.IsNullOrEmpty();

            return state;
        }

        public virtual async Task<ForgeInfrastructureState> SetSetupStep(ForgeInfrastructureSetupStepTypes step)
        {
            state.SetupStep = step;

            return state;
        }
        #endregion

        #region Helpers

        #endregion
    }
}
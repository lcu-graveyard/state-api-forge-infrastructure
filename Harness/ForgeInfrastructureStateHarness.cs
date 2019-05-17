using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Fathym;
using Fathym.Design.Singleton;
using LCU.Graphs;
using LCU.Graphs.Registry.Enterprises;
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
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        { 
            idGraph = req.LoadGraph<IdentityGraph>(log);
        }
        #endregion

        #region API Methods
        public virtual async Task<ForgeInfrastructureState> ConfigureInfrastructure(string infraType, bool useDefaultSettings, MetadataModel settings)
        {
            

            state.InfrastructureConfigured = true;
            
            state.SetupStep = null;
            
            return state;
        }
        
        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            var gitHubToken = await idGraph.RetrieveThridPartyAccessToken(details.Username, "GIT-HUB");

            //Temp - Should load whether or not it actually is
            state.InfrastructureConfigured = false;

            //Temp - Should load whether or not it actually is
            state.SourceControlConfigured = false;

            state.SetupStep = null;
            
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
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
using LCU.State.API.Devices.ConfigManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace LCU.State.API.Forge.Harness
{
    public class ForgeInfrastructureStateHarness : LCUStateHarness<ForgeInfrastructureState>
    {
        #region Fields
        #endregion

        #region Properties

        #endregion

        #region Constructors
        public ForgeInfrastructureStateHarness(HttpRequest req, ILogger log, ForgeInfrastructureState state)
            : base(req, log, state)
        { }
        #endregion

        #region API Methods
        public virtual async Task<ForgeInfrastructureState> ConfigureInfrastructure(string infraType, string template)
        {
            //  TODO:  Set it up

            state.InfrastructureConfigured = true;
            
            return state;
        }
        
        public virtual async Task<ForgeInfrastructureState> Ensure()
        {
            //Temp - Should load whether or not it actually is
            state.InfrastructureConfigured = false;
            
            return state;
        }
        #endregion

        #region Helpers

        #endregion
    }
}
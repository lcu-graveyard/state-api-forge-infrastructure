using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using LCU.State.API.Forge.Harness;
using LCU.State.API.Devices.ConfigManager.Models;
using System.Runtime.Serialization;

namespace LCU.State.API.Forge.Infrastructure
{
    [Serializable]
    [DataContract]
    public class ConfigureInfrastructureRequest
    {
        [DataMember]
        public virtual string InfrastructureType {get; set;}

        [DataMember]
        public virtual string Template {get; set;}
    }
    
    public static class ConfigureInfrastructure
    {
        [FunctionName("ConfigureInfrastructure")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<ConfigureInfrastructureRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                await mgr.ConfigureInfrastructure(reqData.InfrastructureType, reqData.Template);

                return await mgr.WhenAll(
                    
                );
            });
        }
    }
}

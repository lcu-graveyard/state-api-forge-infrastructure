using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using Fathym;
using LCU.State.API.Forge.Infrastructure.Models;
using LCU.State.API.Forge.Infrastructure.Harness;

namespace LCU.State.API.Forge.Infrastructure
{
    [Serializable]
    [DataContract]
    public class ConfigureDevOpsRequest
    {
        [DataMember]
        public virtual string NPMAccessToken { get; set; }
        
        [DataMember]
        public virtual string NPMRegistry { get; set; }
    }

    public static class ConfigureDevOps
    {
        [FunctionName("ConfigureDevOps")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<ConfigureDevOpsRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Configuring DevOps: {reqData.NPMRegistry}");

                await mgr.ConfigureDevOps(reqData.NPMRegistry, reqData.NPMAccessToken);

                return await mgr.WhenAll(
                    mgr.Ensure()
                );
            });
        }
    }
}

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
using LCU.State.API.Forge.Infrastructure.Models;
using LCU.State.API.Forge.Infrastructure.Harness;
using Fathym;

namespace LCU.State.API.Forge.Infrastructure
{
    [Serializable]
    [DataContract]
    public class ConfigureInfrastructureRequest
    {
        [DataMember]
        public virtual string InfrastructureType { get; set; }

        [DataMember]
        public virtual MetadataModel Settings { get; set; }

        [DataMember]
        public virtual bool UseDefaultSettings { get; set; }
    }

    public static class ConfigureInfrastructure
    {
        [FunctionName("ConfigureInfrastructure")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            return await req.Manage<ConfigureInfrastructureRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                await mgr.ConfigureInfrastructure(reqData.InfrastructureType, reqData.UseDefaultSettings, reqData.Settings);

                await mgr.Ensure();

                return await mgr.WhenAll(
                    mgr.LoadInfrastructureRepository()
                );
            });
        }
    }
}

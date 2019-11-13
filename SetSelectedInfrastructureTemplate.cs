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

namespace LCU.State.API.Forge.Infrastructure
{
    [Serializable]
    [DataContract]
    public class SetSelectedInfrastructureTemplateRequest
    {
        [DataMember]
        public virtual string Template { get; set; }
    }

    public static class SetSelectedInfrastructureTemplate
    {
        [FunctionName("SetSelectedInfrastructureTemplate")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<SetSelectedInfrastructureTemplateRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Setting Selected Infrastructure Template: {reqData.Template}");

                await mgr.SetSelectedInfrastructureTemplate(reqData.Template);

                return await mgr.WhenAll(
                );
            });
        }
    }
}

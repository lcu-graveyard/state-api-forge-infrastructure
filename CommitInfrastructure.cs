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
    public class CommitInfrastructureRequest
    {
        [DataMember]
        public virtual string Template { get; set; }
    }

    public static class CommitInfrastructure
    {
        [FunctionName("CommitInfrastructure")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            return await req.Manage<CommitInfrastructureRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Committing Infrastructure: {reqData.Template}");

                await mgr.CommitInfrastructure(reqData.Template);

                await mgr.Ensure();

                await mgr.HasProdConfig();

                return await mgr.WhenAll(
                    mgr.LoadInfrastructureRepository()
                );
            });
        }
    }
}

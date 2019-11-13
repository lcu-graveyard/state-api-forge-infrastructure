using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using LCU.State.API.Forge.Infrastructure.Models;
using LCU.State.API.Forge.Infrastructure.Harness;
using System.Runtime.Serialization;

namespace LCU.State.API.Forge.Infrastructure
{
    [Serializable]
    [DataContract]
    public class CreateAppFromSeedRequest
    {
        [DataMember]
        public virtual string Name { get; set; }
    }

    public static class CreateAppFromSeed
    {
        [FunctionName("CreateAppFromSeed")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            return await req.Manage<CreateAppFromSeedRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Creating App from Seed: {context.FunctionAppDirectory}\\..  {reqData.Name}");

                await mgr.CreateAppFromSeed($"{context.FunctionAppDirectory}\\..", reqData.Name);

                return await mgr.WhenAll(
                );
            });
        }
    }
}

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
    public class AppSeedCompleteCheckRequest
    {
    }

    public static class AppSeedCompleteCheck
    {
        [FunctionName("AppSeedCompleteCheck")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            return await req.Manage<AppSeedCompleteCheckRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                log.LogInformation($"Performing App Seed Complete Check: {context.FunctionAppDirectory}\\..");

                await mgr.AppSeedCompleteCheck($"{context.FunctionAppDirectory}\\..");

                return await mgr.WhenAll(
                );
            });
        }
    }
}

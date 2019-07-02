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
    public class SetupGitHubOAuthRequest
    {
        [DataMember]
        public virtual string ClientID { get; set; }
        
        [DataMember]
        public virtual string ClientSecret { get; set; }
    }

    public static class SetupGitHubOAuth
    {
        [FunctionName("SetupGitHubOAuth")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Admin, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            return await req.Manage<SetupGitHubOAuthRequest, ForgeInfrastructureState, ForgeInfrastructureStateHarness>(log, async (mgr, reqData) =>
            {
                await mgr.SetupGitHubOAuth(reqData.ClientID, reqData.ClientSecret);

                return await mgr.WhenAll(
                );
            });
        }
    }
}

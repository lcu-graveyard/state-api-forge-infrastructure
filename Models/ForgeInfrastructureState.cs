using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace LCU.State.API.Devices.ConfigManager.Models
{
    [DataContract]
    public class ForgeInfrastructureState
    {
        [DataMember]
        public virtual bool InfrastructureConfigured { get; set; }

        [DataMember]
        public virtual bool Loading { get; set; }
    }
}
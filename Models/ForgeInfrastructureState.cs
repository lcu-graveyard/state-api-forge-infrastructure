using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LCU.State.API.Forge.Infrastructure.Models
{
    [DataContract]
    public class ForgeInfrastructureState
    {
        [DataMember]
        public virtual bool InfrastructureConfigured { get; set; }

        [DataMember]
        public virtual bool Loading { get; set; }

        [DataMember]
        [JsonConverter(typeof(StringEnumConverter))]
        public virtual ForgeInfrastructureSetupStepTypes? SetupStep { get; set; }
    
        [DataMember]
        public virtual bool SourceControlConfigured { get; set; }
}

    [DataContract]
    public enum ForgeInfrastructureSetupStepTypes {
        [EnumMember]
        Azure,
        
        [EnumMember]
        AWS,
        
        [EnumMember]
        Custom
    }
}
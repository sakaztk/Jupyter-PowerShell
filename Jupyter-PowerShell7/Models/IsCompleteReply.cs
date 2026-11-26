using Newtonsoft.Json;

namespace Jupyter_PowerShell7.Models
{
    public class IsCompleteReply
    {
        [JsonProperty("status")]
        public string Status { get; set; } = "complete";

        [JsonProperty("indent")]
        public string Indent { get; set; } = string.Empty;
    }
}

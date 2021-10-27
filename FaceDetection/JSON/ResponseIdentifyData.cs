using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceRecognition_Simple_Sample.JSON.A2I
{
    public class ResponseIdentifyData
    {
        /// <summary>
        /// 客戶ID(Ex:AH051)
        /// </summary>
        public string CustomerID { get; set; } = string.Empty;

        /// <summary>
        /// 使用者ID(Ex:AH05101，辨識失敗為空值)
        /// </summary>
        public string UserID { get; set; } = string.Empty;

        /// <summary>
        /// 狀態(Yes/No)
        /// </summary>
        public string Status { get; set; } = "No";

        /// <summary>
        /// 系統執行類型(註冊/辨識/註冊結果/辨識結果)
        /// </summary>
        public string Type { get; set; } = "FaceIdentifyResult";
    }
}

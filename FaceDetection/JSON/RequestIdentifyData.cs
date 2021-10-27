using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FaceRecognition_Simple_Sample.JSON.A2I
{
    public class RequestIdentifyData
    {
        /// <summary>
        /// 服務名稱
        /// </summary>
        public string Service { get; set; } = string.Empty;

        /// <summary>
        /// 人臉影像(Basie64)
        /// </summary>
        public string FaceImage { get; set; } = string.Empty;

        /// <summary>
        /// 客戶ID(Ex:AH051)
        /// </summary>
        public string CustomerID { get; set; } = string.Empty;

        //TODO:20200908新增(做Offline測試)
        /// <summary>
        /// 用戶編號
        /// </summary>
        public string UserID { get; set; } = string.Empty;

        /// <summary>
        /// 系統執行類型(註冊/辨識/註冊結果/辨識結果)
        /// </summary>
        public string Type { get; set; } = "FaceIdentify";
    }
}

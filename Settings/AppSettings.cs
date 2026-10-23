using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Settings
{
    public class AppSettings
    {
        public string User { get; set; } = "";

        public string Password { get; set; } = "";

        public string UserEnter { get; set; } = "{User}, входит";

        public string UserLeave { get; set; } = "{User}, выходит";
    }
}

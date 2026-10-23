using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlcoRats.Settings
{
    class AppSettings : ObservableObject
    {
        public string UserEnter { get; } = "{User}, входит";

        public string UserLeave { get; } = "{User}, выходит";
    }
}

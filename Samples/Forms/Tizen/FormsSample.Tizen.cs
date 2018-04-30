using System;
using Tizen.Security;

namespace FormsSample.Tizen
{
    class Program : global::Xamarin.Forms.Platform.Tizen.FormsApplication
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            CheckResult result = PrivacyPrivilegeManager.CheckPermission("http://tizen.org/privilege/camera");
            switch (result)
            {
                case CheckResult.Allow:
                    LoadApplication(new App());
                    break;
                default:
                    break;
            }
        }

        static void Main(string[] args)
        {
            var app = new Program();
            global::Xamarin.Forms.Platform.Tizen.Forms.Init(app);
            global::ZXing.Net.Mobile.Forms.Tizen.Platform.Init();
            app.Run(args);
        }
    }
}

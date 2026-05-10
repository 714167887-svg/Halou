using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace JsqClipboardCadPlugin
{
    public sealed class HalouAuthLispBridge
    {
        // AutoLISP: (halou-auth "zk") -> T / nil
        [LispFunction("halou-auth")]
        public ResultBuffer CheckFeatureAuth(ResultBuffer args)
        {
            try
            {
                if (args == null)
                {
                    return null;
                }

                TypedValue[] arr = args.AsArray();
                if (arr == null || arr.Length < 1)
                {
                    return null;
                }

                string featureId = arr[0].Value as string;
                if (string.IsNullOrWhiteSpace(featureId))
                {
                    return null;
                }

                bool allowed = ClipboardCommands.IsFeatureAuthorizedForLisp(featureId.Trim());
                if (!allowed)
                {
                    return null;
                }

                return new ResultBuffer(new TypedValue((int)LispDataType.T_atom));
            }
            catch
            {
                return null;
            }
        }
    }
}

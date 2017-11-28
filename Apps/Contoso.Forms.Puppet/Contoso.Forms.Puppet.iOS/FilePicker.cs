﻿using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Contoso.Forms.Puppet.iOS;
using Foundation;
using MobileCoreServices;
using Photos;
using UIKit;
using Xamarin.Forms;

// Make this class visible to the DependencyService manager.
[assembly: Dependency(typeof(FilePicker))]

namespace Contoso.Forms.Puppet.iOS
{
    public class FilePicker : IFilePicker
    {
        public Task<string> PickFile()
        {
            var taskCompletionSource = new TaskCompletionSource<string>();
            var imagePicker = new UIImagePickerController();
            imagePicker.FinishedPickingMedia += (sender, args) => {
                taskCompletionSource.SetResult(args.ReferenceUrl?.AbsoluteString);
                imagePicker.DismissModalViewController(true);
            };
            imagePicker.Canceled += (sender, args) => {
                taskCompletionSource.SetResult(null);
                imagePicker.DismissModalViewController(true);
            };
            UIWindow window = UIApplication.SharedApplication.KeyWindow;
            var viewController = window.RootViewController;
            viewController.PresentModalViewController(imagePicker, true);
            return taskCompletionSource.Task;
        }

        public Tuple<byte[], string, string> ReadFile(string file)
        {
            Tuple<byte[], string, string> result = null;
            var asset = PHAsset.FetchAssets(new[] { new NSUrl(file) }, null).LastObject as PHAsset;
            if (asset != null)
            {
                var options = new PHImageRequestOptions { Synchronous = true };
                PHImageManager.DefaultManager.RequestImageData(asset, options,(data, dataUti, orientation, info) => {
                    var extension = new NSUrl(dataUti).PathExtension;
                    var uti = UTType.CreatePreferredIdentifier(UTType.TagClassFilenameExtension, extension, null);
                    var mime = UTType.GetPreferredTag(uti, UTType.TagClassMIMEType);
                    var dataBytes = new byte[data.Length];
                    Marshal.Copy(data.Bytes, dataBytes, 0, Convert.ToInt32(data.Length));
                    result = new Tuple<byte[], string, string>(dataBytes, dataUti, mime);
                });
            }
            return result;
        }
    }
}

#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
#endif

open System
open System.Linq
open System.Threading.Tasks

#if !XAMARIN
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Xaml
open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Devices
#else
open Xamarin.Forms
open Xamarin.Forms.Xaml
open Xamarin.Essentials
#endif
open Fsdk

open GWallet.Backend

type LoadingPage() as self =
    inherit ContentPage()

    let _ = base.LoadFromXaml(typeof<LoadingPage>)
    do
        self.Init()

    member self.Transition(): unit =
        async {
            MainThread.BeginInvokeOnMainThread(fun _ ->
                let balancesPage = BalancesPage() :> Page
                NavigationPage.SetHasNavigationBar(balancesPage, true)

                let navPage = new NavigationPage(balancesPage)

                self.Navigation.InsertPageBefore(navPage, self)
                self.Navigation.PopAsync().ContinueWith((fun (t: Task) -> 
                    Android.Util.Log.Debug("Custom" ,sprintf "Exception: %A" t.Exception) |> ignore
                ), TaskContinuationOptions.OnlyOnFaulted)
                |> ignore
            )
        }
        |> Async.Start

    member self.Init (): unit =
        self.Transition()


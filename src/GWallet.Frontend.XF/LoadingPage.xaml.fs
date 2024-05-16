#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
#endif

open System
open System.Linq

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
            let balancesPage () =
                BalancesPage()
                    :> Page
            FrontendHelpers.SwitchToNewPageDiscardingCurrentOne self balancesPage
        }
            |> FrontendHelpers.DoubleCheckCompletionAsync false

        ()

    member self.Init (): unit =
        self.Transition()


#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
// We have unused variables because they are in the #XAMARIN sections.
// this should be removed once we fully integrate this code.
#nowarn "1182"
#endif

open System
open System.Linq
open System.Threading
open System.Threading.Tasks

#if !XAMARIN
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Xaml
open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.Networking
#else
open Xamarin.Forms
open Xamarin.Forms.Xaml
open Xamarin.Essentials
#endif

open Fsdk

#if XAMARIN
open GWallet.Frontend.XF.Controls
#else
open GWallet.Frontend.Maui.Controls
#endif
open GWallet.Backend
open GWallet.Backend.FSharpUtil.UwpHacks


type FiatAmountFrameTapHandler = unit -> unit

// this type allows us to represent the idea that if we have, for example, 3 LTC and an unknown number of ETC (might
// be because all ETC servers are unresponsive), then it means we have AT LEAST 3LTC; as opposed to when we know for
// sure all balances of all currencies because all servers are responsive
type TotalBalance =
    | ExactBalance of decimal
    | AtLeastBalance of decimal
    static member (+) (x: TotalBalance, y: decimal) =
        match x with
        | ExactBalance exactBalance -> ExactBalance (exactBalance + y)
        | AtLeastBalance exactBalance -> AtLeastBalance (exactBalance + y)
    static member (+) (x: decimal, y: TotalBalance) =
        y + x

type BalancesPage() =
    inherit ContentPage()

    let _ = base.LoadFromXaml(typeof<BalancesPage>)

    member self.OnButtonClicked(sender: obj, e: EventArgs) =
        FrontendHelpers.SwitchToNewPage self (fun () -> ReceivePage()) true

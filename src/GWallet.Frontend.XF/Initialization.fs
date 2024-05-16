#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
#endif

open System.Linq

#if XAMARIN
open Xamarin.Forms
#else
open Microsoft.Maui
open Microsoft.Maui.Controls
#endif

open GWallet.Backend

module Initialization =

    let internal LandingPage(): NavigationPage =
        let landingPage:Page = LoadingPage ()

        let navPage = NavigationPage landingPage
        NavigationPage.SetHasNavigationBar(landingPage, false)
        navPage

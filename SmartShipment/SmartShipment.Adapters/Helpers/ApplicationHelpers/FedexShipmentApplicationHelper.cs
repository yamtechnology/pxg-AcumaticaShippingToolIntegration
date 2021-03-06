﻿using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using SmartShipment.Adapters.Cache;
using SmartShipment.Adapters.Common;
using SmartShipment.Adapters.Helpers.ControlHelpers;
using SmartShipment.Adapters.Map;
using SmartShipment.AutomationUI.UIAutomation;
using SmartShipment.Information;
using SmartShipment.Information.Properties;
using SmartShipment.Network.Mapping;
using SmartShipment.Settings;

namespace SmartShipment.Adapters.Helpers.ApplicationHelpers
{
    public class FedexShipmentApplicationHelper : ShipmentApplicationHelperBase, IShipmentApplicationHelper
    {
        private readonly ISettings _settings;
        private readonly ISmartShipmentMessagesProvider _messagesProvider;
        private readonly IShipmentAutomationCache _cache;
        private AutomationElement _mainWindow;       

        private FedexShipManagerMap _fedexShipManagerMap;
        private AutomationElement _packageMainWindowDialog;
        private FedExShipMahagerShellMap _fedExShipMahagerShellMap;
        

        public FedexShipmentApplicationHelper(ISettings settings, ISmartShipmentMessagesProvider messagesProvider, IShipmentAutomationCache cache)
        {
            _settings = settings;
            _messagesProvider = messagesProvider;
            _cache = cache;
        }

        public bool RunShipmentApplication(string shipmentNumber)
        {
            _messagesProvider.Log(InformationResources.INFO_FEDEX_STARTING_APPLICATION);
            var process = RunApplication(_settings.FedexProcessName);
            if (process != null)
            {
                _mainWindow = AutomationElement.RootElement.FindChildByProcessId(process.Id);
                if (_mainWindow != null)
                {
                    _messagesProvider.Log(InformationResources.INFO_FEDEX_START_APPLICATION);
                    return true;
                }
                else
                {
                    _messagesProvider.Warn(string.Format(InformationResources.WARN_NO_PROCESS_INIT_FOR_FEDEX_APPLICATION, shipmentNumber));
                    return false;
                }
                
            }
            else
            {
                _messagesProvider.Warn(string.Format(InformationResources.WARN_NO_PROCESS_FOUND_FOR_FEDEX_APPLICATION, shipmentNumber));
                return false;
            }           
        }

        public bool PopulateApplicaitonControlMap()
        {
            //Try set UI for input shipment           
            _fedExShipMahagerShellMap =  (FedExShipMahagerShellMap)_cache.Get(ShipmentApplicaotinHelperType.FedExShipManagerShellMap, _mainWindow);
            _fedExShipMahagerShellMap.ClearUIForStartInput();

            //Find and map UI controls
            _fedexShipManagerMap = (FedexShipManagerMap)_cache.Get(ShipmentApplicaotinHelperType.FedexShipManagerMap, _mainWindow);
            _fedexShipManagerMap.Map();
            
            if (NeedClearing(_fedexShipManagerMap.ShipmentAutomationControls))
            {
                _messagesProvider.Info(InformationResources.WARN_CURRENT_SHIPMENT_SCREEN_ISNOT_EMPTY);
                return false;
            }
            _fedExShipMahagerShellMap.BottomToolboxControl?.GetButtons()?[0].Click();
            _messagesProvider.Log(InformationResources.INFO_FEDEX_INIT_CONTROL_MAP);
            return true;
        }

        public void PopulateApplicaitonByShipmentData(ShipmentMapper shipment)
        {
            StartTimer(_fedExShipMahagerShellMap);

            try
            {
                //Shipment informaiton
                PrepareControlsBeforeWorkflow(shipment);
                SendShipmentData(shipment);
                Wait(1);
                if (CheckShipmentProgrammWarnings(_messagesProvider)) return;

                //Shipment informaiton - email
                SetEmail(shipment);
                Wait(1);
                if (CheckShipmentProgrammWarnings(_messagesProvider)) return;

                //Package informaiton
                if (CheckShipmentFieldsFilled(_fedexShipManagerMap.RequiredShipmentPane, _messagesProvider))
                {
                    _messagesProvider.Log(InformationResources.INFO_FEDEX_FILL_SHIPMENT_DATA);
                    SendPackagesData(shipment);
                }
            }
            finally
            {
               StopTimer();
            }

            CheckShipmentProgrammWarnings(_messagesProvider);

        }

        #region Workflow        

        private void PrepareControlsBeforeWorkflow(ShipmentMapper shipment)
        {
            if (shipment.Contact.Address.Country.Value == "US")
            {
                _fedexShipManagerMap.PostalCode.IsClearMask = true;
                _fedexShipManagerMap.PostalCode.MaxLength = 9;
            }

            if (shipment.Contact.Address.Country.Value == "US" || shipment.Contact.Address.Country.Value == "CA")
            { 
                _fedexShipManagerMap.Telephone.IsClearMask = true;
                _fedexShipManagerMap.Telephone.MaxLength = 10;
            }
        }

        private void SendShipmentData(ShipmentMapper shipment)
        {
            _fedexShipManagerMap.MapShipmentData(shipment);
            _fedexShipManagerMap.SaveUpdateAddressBook.SetValueToControl(_settings.FedexAddUpdateAddressBook? "true" : "false");           
        }

        private void SetEmail(ShipmentMapper shipment)
        {
            var fedexShipAlertTabMap = (FedExShipAlertTabMap)_cache.Get(ShipmentApplicaotinHelperType.FedExShipAlertTabMap, _mainWindow);
            var tabItems = _fedExShipMahagerShellMap.TopTabControl.GetTabItems();
            if (tabItems.Any() && tabItems.Count > 2)
            {
                var selectedTabItem = tabItems[2];
                selectedTabItem.Select();
                var searchPatch = fedexShipAlertTabMap.Email.DescendentIdPath;
                var emailPane = selectedTabItem.AutomationElement.FindDescendentByIdPath(searchPatch);
                if (emailPane != null)
                {
                    fedexShipAlertTabMap.Email.Init(emailPane);
                    fedexShipAlertTabMap.MapShipmentData(shipment);
                }
                tabItems[0].Select();
            }
        }

        private void SendPackagesData(ShipmentMapper shipment)
        {
            if (!shipment.Packages.Any()) return;                       

            var packageNumbers = shipment.Packages.Count;
            var packagesWeight = shipment.Packages.Sum(p => p.Weight.Value);
            
            _fedexShipManagerMap.PackageNumbers.SetValueToControl(packageNumbers.ToString());
            _fedexShipManagerMap.PackageWeight.SetValueToControl(packagesWeight.ToString());

            _fedexShipManagerMap.PackageServiceType.SetValueToControl("R");

            //PackageType always disabled for "R"
            if (!_fedexShipManagerMap.PackageServiceType.GetCurrentValue().StartsWith("R"))
            {
                _fedexShipManagerMap.PackageType.SetValueToControl("1");
            }

            _fedexShipManagerMap.PackageBillTransportationTo.SetValueToControl("1");

            if (packageNumbers > 1)
            {
                MultiPackageWindowProcess(shipment);
            }
        }

        #endregion

        #region Multi-piece shipment dialog

        private void MultiPackageWindowFindMainDialog(AutomationElement parent)
        {
            _packageMainWindowDialog = parent.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));            
        }

        private void MultiPackageWindowProcess(ShipmentMapper shipment)
        {
            StopTimer();
            var delayCount = 20;
            while (delayCount > 0 && _packageMainWindowDialog == null)
            {
                MultiPackageWindowWait();
                MultiPackageWindowFindMainDialog(_mainWindow);
                delayCount--;
                Thread.Sleep(100);
            }
            
            if (_packageMainWindowDialog != null)
            {
                var packageMap = new FedExMultiPieceShipmentMap(new ShipmentAutomationUIControlHelper(), _packageMainWindowDialog, _messagesProvider);
                packageMap.Map();
                MultiPackageWindowInsertData(packageMap, shipment);
            }
            else
            {
                _messagesProvider.Warn("Cannot find source for input multipackage data.");
            }
        }

        private void MultiPackageWindowInsertData(FedExMultiPieceShipmentMap packageMap, ShipmentMapper shipment)
        {
            packageMap.PrintLabelsAfterCompleteShipment.SetValueToControl("true");
            foreach (var package in shipment.Packages)
            {                
                packageMap.MapPackageData(package);
                packageMap.PackageAddButton.Click();
            }
            packageMap.PackageSaveAndCloseButton.Click();
            _messagesProvider.Log(InformationResources.INFO_FEDEX_FILL_PACKAGE_DATA);
        }

        private void MultiPackageWindowWait()
        {
            var delayCount = 20;
            while (delayCount > 0 && _mainWindow.FindChildByControlType(ControlType.Window) == null)
            {
                _fedExShipMahagerShellMap.BottomToolboxControl.GetButtons().Last().Click();
                delayCount--;
                Task.Delay(100);
            }
        }

        #endregion
        
    }
}

# Stock hook verification for the stock-UI reservation overlay plan

Checks the claims in `docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md` section 5 (table) and section 7.4.

- **Stock source:** decompiled with `ilspycmd -t <type>` from `Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll` (KSP 1.12.5). Regenerate a dump with the same command when a later PR needs the body.
- **Mod source:** Contract Configurator (CC) and KSPCommunityFixes (KSPCF) from the local copies under `mods/`.
- **Tags:**
  - CONFIRMED: the claim matches the code.
  - REFUTED: the claim is wrong.
  - DIFFERENT: the claim is broadly true, but a detail changes the plan.
- **Snippets:** obfuscation noise (`while(true){switch(N)...}`) is removed from the quoted code.

---

## 1. `KSP.UI.Screens.RDNode` / `RDNodePrefab`

**Signatures**
- `public void UpdateGraphics()`
- `public void SetButtonState(RDNode.State state)`
- `private string GetTooltipCaption()` (no parameters)
- `public RDNodePrefab graphics;`
- `private TooltipController_TitleAndText tooltip;` (the same object as `graphics.tooltip`, which is public)
- `RDNodePrefab`:
  - `public void SetIconColor(Color color)`, which sets `techIcon.color`
  - `public Color GetIconColor()`
  - `public UIStateButton button`
  - `public TooltipController_TitleAndText tooltip`

**Claims**
- **CONFIRMED: `SetButtonState` resets the icon colour.** `RESEARCHED`/`RESEARCHABLE` do `graphics.SetIconColor(Color.white)`. `FADED` does `var c = graphics.GetIconColor(); graphics.SetIconColor(new Color(c.r, c.g, c.b, 0.5f))`.
  - Caveat: `FADED` keeps the current RGB. A Parsek tint on a FADED node therefore survives the next refresh. When the mark clears, the postfix must write white (alpha 0.5 on FADED) itself.
- **CONFIRMED: `UpdateGraphics` calls `SetButtonState` first,** choosing `RESEARCHED`, `RESEARCHABLE` or `FADED` (and `HIDDEN` when `hideIfNoParts`). It then writes the tooltip. A postfix on `UpdateGraphics` runs after both.
- **CONFIRMED: `RDTechTree.RefreshUI()` runs `controller.nodes[i].UpdateGraphics()` for every node,** then `controller.UpdatePanel()`.
- **CONFIRMED: `GetTooltipCaption` returns a `string`, and only `UpdateGraphics` consumes it** (two sites):
  - `tooltip.textString = GetTooltipCaption();`
  - `tooltip.textString = tooltipCaption + "\n" + KSPUtil.PrintCollection(partsAssigned, ...)`
- **CONFIRMED: the tree nodes' tint is safe from the panel.** `RDController.UpdatePanel` calls `node_inPanel.SetButtonState(...)`, but `node_inPanel` is the separate preview node in the side panel, not the tree node.

## 2. `KSP.UI.Screens.RDController`

**Signatures**
- `public void UpdatePanel()`
- `private void UpdatePurchaseButton()`
- `private void ActionButtonClick(string state)`
- `public void ShowNodePanel(RDNode node)`
- `public UIStateButton actionButton;`
- `public List<RDNode> nodes = new List<RDNode>();`
- `public RDPartList partList;`
- `public RDNode node_selected;` (`[NonSerialized]`)
- `public static EventData<RDController> OnRDTreeSpawn` / `OnRDTreeDespawn`

**Claims**
- **CONFIRMED: `RDController.nodes` is public.** The reflection in `StockUiOverlayController.TryGetRdNodes` (`StockUiOverlayController.cs:744`) is unneeded.
- **CONFIRMED: `actionButton` is a `KSP.UI.UIStateButton`,** and `public void Enable(bool enable)` sets `Button.interactable = enable`.
- **CONFIRMED: `UpdatePanel` re-enables Research.** Its RESEARCHABLE path does `actionButton.gameObject.SetActive(true); actionButton.Enable(true); actionButton.SetState("research");`.
- **DIFFERENT: the same `actionButton` has two roles.**
  - On a researched node it is the "purchase all parts" button: `UpdatePanel` does `actionButton.SetState("purchase"); UpdatePurchaseButton();`, which calls `Enable(CanAfford)`.
  - On a FADED node it is hidden (`SetActive(false)`).
  - So the `UpdatePanel` postfix must branch on `node_selected.IsResearched` / state. A blanket `Enable(false)` also kills part purchase.
- **Part-purchase paths in R&D:**
  - The "purchase" state runs `ActionButtonClick("purchase")`. It loops over `node_selected.tech.partsAssigned` and calls `node_selected.tech.PurchasePart(ap)`, then `node_selected.UpdateGraphics(); partList.SetupParts(node_selected); UpdatePanel();`.
  - The per-part R&D rows (`RDPartList` / `RDPartListItem.Setup(string label, string buttonState, AvailablePart, PartUpgradeHandler.Upgrade)`, button state `"purchaseable"`) use `PartListTooltipController`, the same `PartListTooltip` as the editor (section 8).
- **Research backstop:** the "research" state runs `node_selected.tech.ResearchTech()`. Parsek's `TechResearchSpendPatch` already prefixes that method.

## 3. `Strategies.Strategy`

**Signatures**
- `public bool CanBeActivated(out string reason)`, `public bool CanBeDeactivated(out string reason)`: both non-virtual.
- `public bool Activate()`, `public bool Deactivate()`, `public void Update()`: all non-virtual.
- `protected virtual bool CanActivate(ref string reason)`, `protected virtual bool CanDeactivate(ref string reason)`.
- `public void Register()`, `public void Unregister()`, `public void Load(ConfigNode node)`.
- `public bool IsActive => isActive` (`private bool isActive`); `public double DateActivated` (`private double dateActivated`).

**Claims**
- **CONFIRMED: `Activate()` is gated on `CanBeActivated` and charges the setup cost.**
  ```
  if (CanBeActivated(out _)) { isActive = true; Register(); dateActivated = Planetarium.fetch.time;
    if (InitialCostFunds != 0f) Funding.Instance.AddFunds(-Mathf.Abs(InitialCostFunds), TransactionReasons.StrategySetup);
    ... Reputation / Science likewise ... return true; }
  ```
  One postfix on `CanBeActivated` covers both the button and `Activate()`.
- **CONFIRMED: `Deactivate()` is gated on `CanBeDeactivated`.** It does `if (CanBeDeactivated(out _)) { isActive = false; Unregister(); return true; } return false;` and charges nothing.
- **CONFIRMED (with a large caveat, see Surprises): `Update()` auto-expiry.**
  - Code: `if (LongestDuration != 0 && dateActivated + LongestDuration <= now) { SendStateMessage(...expired...); Deactivate(); return; }`.
  - `StrategySystem.Update()` (private) calls `strategies[i].Update()` only for `IsActive` strategies.
  - A refused `Deactivate()` therefore re-sends the expiry message every frame. The doc's hazard is real.
- **Stock `CanBeDeactivated` body:**
  - `reason = autoLOC_304887; if (LeastDuration != 0 && dateActivated + LeastDuration < now) { reason = autoLOC_304890; return false; }`, then `CanDeactivate(ref reason)`.
  - The comparison is inverted (refuses AFTER the minimum duration). KSPCF `StrategyDuration` transpiles it (`Bge_Un_S -> Ble_Un_S`).
- **SURPRISE: in pure stock, both duration getters return 0.**
  - `LeastDuration => FactorLerp(minLeastDuration, maxLeastDuration)` and `LongestDuration => FactorLerp(minLeastDuration, maxLeastDuration)`. This is a Squad copy-paste bug.
  - The protected fields `minLeastDuration` / `maxLeastDuration` are never assigned (`SetupConfig` only sets `Config`).
  - Without KSPCF, both are 0, so auto-expiry and the duration gate are dead code.
  - KSPCF `StrategyDuration` (`mods/KSPCommunityFixes/KSPCommunityFixes/BugFixes/StrategyDuration.cs`) prefixes both getters to read `MinLeastDuration..MaxLongestDuration` from config.
  - KSPCF is installed in BOTH the dev instance and `automation/stock-minimal/GameData`.
- **CONFIRMED: `StrategySystem` has no API to toggle a strategy without charging.** It has `GetStrategies(string dept)`, `HasActiveStrategy`, `HasConflictingActiveStrategies` and `public List<Strategy> Strategies`, and nothing that toggles.
  - Activate without a charge: `Strategy.Load(ConfigNode)` reads `date`/`factor`/`EFFECT` nodes, then sets `isActive = true; Register();` and charges nothing.
  - Deactivate without the gate: `Unregister()` plus reflection `isActive = false`, because `Deactivate()` is gated.
- **Hazard:** stock `CanBeActivated` dereferences `Administration.Instance` first (`Administration.Instance.ActiveStrategyCount >= MaxActiveStrategies`). It throws outside the Administration screen, and a postfix never runs in that case.

## 4. `KSP.UI.Screens.Administration` (player path)

**Signatures**
- `public class Administration.StrategyWrapper` (public nested; `public Strategy strategy`)
- `public void SetSelectedStrategy(StrategyWrapper wrapper)`
- `private void BtnInputAccept(string state)`
- `private void OnAcceptConfirm()`
- `private void OnCancelConfirm()`
- `private void UpdateStrategyDescription(string title, string description, string effects, string reason)`
- `public UIStateButton btnAcceptCancel`
- `private void CreateStrategiesList(List<DepartmentConfig>)`, `private void AddStrategiesListItem(UIList, List<Strategy>)`
- `public void RedrawPanels()`

**Claims**
- **CONFIRMED: the grey row.** `StrategyListItem.SetupButton(bool acceptable, ...)` sets `toggleStateChanger.SetState("na")`, with `invalidColor = "#bdbdbd"`. It is fed `strategies[i].CanBeActivated(out reason)` at list build.
- **CONFIRMED: the orange reason.** `GetStrategyDescription` prepends `"<color=#ff7512>" + reason + "</color>\n\n"`.
- **Player-path hooks for refusing deactivation** (none of these is reached by `Strategy.Update()`):
  - Button state: postfix `Administration.SetSelectedStrategy(StrategyWrapper)`. When `wrapper.strategy.IsActive`, stock does `btnAcceptCancel.SetState("cancel")` and `btnAcceptCancel.Enable(wrapper.strategy.CanBeDeactivated(out reason))`. The postfix calls `btnAcceptCancel.Enable(false)` and puts the reason in via `UpdateStrategyDescription(...)` (private).
  - Click backstop: prefix private `BtnInputAccept(string state)` when `state == "cancel"`. It checks `CanBeDeactivated` again, then spawns the `MultiOptionDialog("StrategyConfirmation", ...)` with `OnCancelConfirm`. Or prefix private `OnCancelConfirm()`, which calls `SelectedWrapper.strategy.Deactivate()`.
  - This makes a blanket `CanBeDeactivated` postfix unnecessary.
- **KSPCF also touches Administration:** `DepartmentHeadImage` postfixes `Administration.AddKerbalListItem`.

## 5. `KSP.UI.Screens.MissionControl`

**Signatures**
- `public void AddItem(Contract contract, bool isAvailable, string label = "")` (single overload)
- `public void UpdateInfoPanelContract(Contract contract)`
- `private void RefreshUIControls()`
- `public void RebuildContractList()`
- `private void RefreshContracts()`: `RebuildContractList(); RefreshUIControls();`, subscribed to `GameEvents.Contract.onContractsListChanged`.
- `private void OnClickAccept()`, `OnClickDecline()`, `OnClickCancel()`
- `private void OnSelectContract(UIRadioButton, UIRadioButton.CallType, PointerEventData)`
- Public fields:
  - `Button btnAccept, btnDecline, btnCancel` (all `UnityEngine.UI.Button`)
  - `TextMeshProUGUI contractText, textMCStats, textDateInfo`
  - `MissionSelection selectedMission`
  - `DisplayMode displayMode`
- `private int maxActiveContracts`
- `public class MissionSelection { public bool isAvailable; public Contract contract; public UIListItem listItem; }`

**Claims**
- **CONFIRMED: the `label` parameter and `MCListItem.Setup(Contract, string label)` drawing it (`title.text = label`).**
- **DIFFERENT: a non-empty label replaces the whole title.**
  - Stock: `if (label.Equals("")) mCListItem.Setup(contract, "<color=#fefa87>" + contract.Title + "</color>"); else mCListItem.Setup(contract, label);`
  - The prefix must build the full title text itself, colour included.
  - Archive rows already pass labels (for example `"<color=#00ff00>Completed </color> <color=#fefa87>Title</color>"`), so the prefix must append, not overwrite.
  - `RebuildContractList` calls `AddItem` for every row on every tab switch, so the label is re-applied.
- **CONFIRMED: `UpdateInfoPanelContract` builds `contractText` as `contractText.text = contract.MissionControlTextRich();`** (`public virtual string`). A postfix that appends there works, as KSPCF's does.
- **CONFIRMED: `btnDecline.interactable` / `btnCancel.interactable` are set in `UpdateInfoPanelContract`,** and only there:
  - Active branch: `btnCancel.interactable = selectedMission.contract.CanBeCancelled();`
  - Offered branch: `btnDecline.interactable = selectedMission.contract.CanBeDeclined();`
  - Both read `selectedMission.contract`, not the `contract` parameter.
- **DIFFERENT: `RefreshUIControls` is the wrong seam for a per-contract Accept block.**
  - It reads no selection. It sets `btnAccept.interactable = activeContractCount < maxActiveContracts`, globally, and writes `textMCStats`.
  - It runs at `Start`, on `onContractsListChanged`, and after each OnClick*. It does not run on selection: `OnSelectContract` only sets `selectedMission` and calls `UpdateInfoPanelContract`.
  - Recommended fix:
    - Postfix `UpdateInfoPanelContract` to set `btnAccept.interactable = false` when `selectedMission.contract` is Offered and blocked.
    - Also postfix `RefreshUIControls` to re-assert it when `selectedMission != null`, because it rewrites `true` after a list change.
- **CONFIRMED: the slot limit.**
  - `Start()`: `maxActiveContracts = GameVariables.Instance.GetActiveContractsLimit(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));`
  - The signature is `public virtual int GetActiveContractsLimit(float mCtrlNormLevel)`.
  - The count is `ContractSystem.Instance.GetActiveContractCount()` (public int).
- **Null guard needed:** CC calls `MissionControl.Instance.UpdateInfoPanelContract(null)` for contract-type rows (`MissionControlUI.cs:1468`). Stock handles null (hides the buttons), but a Parsek postfix must null-guard.

## 6. `Contracts.Contract`

**Signatures**
- `public virtual bool CanBeCancelled()` and `public virtual bool CanBeDeclined()`: the base returns `true`.
- `public bool Accept()`, `public bool Decline()`, `public bool Cancel()`: all non-virtual.
- `protected virtual void PenalizeCancellation()`
- `public virtual string MissionControlTextRich()`

**Behaviour**
- `Accept`: `if (state == Offered) { dateAccepted = GameTime; floating deadline...; SetState(Active); return true; } return false;`
- `Decline`: `if (state == Offered) { dateFinished = GameTime; SetState(Declined); if (RepLossDeclined > 0) Reputation.Instance.AddReputation(-RepLossDeclined, ContractDecline); return true; }`
- `Cancel`: `if (state == Active) { dateFinished = GameTime; SetState(Cancelled); return true; }`
- `SetState(Cancelled)` runs `PenalizeCancellation(); OnCancelled(); OnFinished(); ... onCancelled / onFailed / onFinished`.
- `PenalizeCancellation` lerps `FundsAdvance -> FundsFailure` over the time elapsed.
- `Decline()` / `Cancel()` do NOT check `CanBeDeclined` / `CanBeCancelled` themselves. A prefix returning false blocks the state change and the penalty.

**Claims**
- **CONFIRMED:** the `Decline()` / `Cancel()` prefix backstop is sound.
- **Other caller:** `ContractSystem.RebuildContracts()` (public) calls `contracts[i].Cancel()` on every Active contract. A `Cancel` prefix also refuses there.
- **REFUTED: "postfix `Contract.CanBeDeclined` / `CanBeCancelled` plus CC's override" is enough. Many STOCK contract types override both methods without calling base:**
  - `FinePrint.Contracts.*`: `ARMContract`, `BaseContract`, `CometSampleContract`, `ExplorationContract` (returns `false` for both), `ISRUContract`, `SatelliteContract`, `StationContract`, `SurveyContract`, `TourismContract`
  - `SentinelMission.SentinelContract`, `SentinelMission.CometDetectionContract`
  - `Contracts.Templates.OrbitalConstructionContract`, `RoverConstructionContract`, `VesselRepairContract`
  - Only `PartTest`, `CollectScience`, `GrandTour`, `PlantFlag`, `RecoverAsset` and the Serenity/ROC contracts use the base.
  - Fix: set `btnDecline` / `btnCancel.interactable` in the `UpdateInfoPanelContract` postfix. It is the single place stock reads these methods, and CC's `OnSelectContract` calls it too.
  - Alternative: enumerate `AccessTools.AllTypes()` subclasses of `Contract` that declare an override, and patch each one.

## 7. `KSP.UI.Screens.AstronautComplex` / `KSP.UI.CrewListItem` / `KSP.UI.TooltipTypes.TooltipController_CrewAC`

**`AstronautComplex` signatures** (all private)
- `void AddItem_Applicants(ProtoCrewMember crew)`
- `void AddItem_Available(ProtoCrewMember crew)`
- `void AddItem_Assigned(string name, float courage, float stupidity, CrewListItem.KerbalTypes type, string label, ProtoCrewMember crew)`
- `void AddItem_Kia(ProtoCrewMember crew)`
- `CrewListItem AddItem(UIList list, CrewListItem widget)`
- `void UpdateCrewCounts()`
- `void SetApplicantsListUnlocked(bool unlocked, string lockReasonTitle = "", string lockReasonCaption = "")`
- `void HireRecruit(UIList, UIList, UIListItem)`

**`CrewListItem` signatures**
- `public void SetLabel(string label)` (writes the private `TextMeshProUGUI label`)
- `public void SetTooltip(ProtoCrewMember crew)`, which calls `tooltipController.SetTooltip(crew)`
- `public void SetButtonEnabled(bool state, string disabledReasonTitle = "", string disabledReasonCaption = "")`
- `public bool MouseoverEnabled { get; set; }`
- `public void SetCrewRef(ProtoCrewMember)`
- `private TooltipController_CrewAC tooltipController` (`[SerializeField]`)
- `public TextMeshProUGUI kerbalName, xp_trait`; `public RawImage kerbalSprite`

**`TooltipController_CrewAC` signatures**
- `public string titleString`, `public string descriptionString`, `public bool showTooltip`
- `public void SetTooltip(ProtoCrewMember pcm, string noHireTitle = "", string noHireDescription = "")`

**Claims**
- **DIFFERENT: every `AddItem_*` returns `void`,** not the `CrewListItem`. A postfix has to take the row by one of these routes:
  - the list's last item (`scrollList*.GetUilistItemAt(Count-1)`);
  - a postfix on `CrewListItem.SetTooltip(ProtoCrewMember)`, which is the LAST call in all four `AddItem_*` and in `BaseCrewAssignmentDialog.AddAvailItem`, so one hook covers both screens.
- **CONFIRMED: `SetLabel` works in a postfix.** Every `AddItem_*` calls `SetLabel` before `SetTooltip`, so a postfix may overwrite it.
- **CONFIRMED: `SetButtonEnabled`.**
  - Body: `if (state) { MouseoverEnabled = true; tooltipController.SetTooltip(crew); } else { MouseoverEnabled = false; tooltipController.SetTooltip(crew, title, caption); }`
  - It needs `crew`, which `SetCrewRef` sets.
- **DIFFERENT: appending to the tooltip.** `SetButtonEnabled` calls `tooltipController.SetTooltip` directly, NOT `CrewListItem.SetTooltip`, and that call rebuilds `descriptionString` from `string.Empty`.
  - An append in a `CrewListItem.SetTooltip` postfix is wiped whenever `UpdateCrewCounts` re-runs `SetApplicantsListUnlocked`. Patch `TooltipController_CrewAC.SetTooltip(PCM, string, string)` instead.
  - Also set `showTooltip = true`: stock leaves it false when both XP and G-limits are off.
  - Stock already appends an inactive line the same way: `if (pcm.inactive) { showTooltip = true; descriptionString += Localizer.Format("#autoLOC_445772", ...inactiveTimeEnd); }`.
- **CONFIRMED: `UpdateCrewCounts` re-unlocks applicants.**
  - Under the crew limit it calls `SetApplicantsListUnlocked(unlocked: true)`, which runs `SetButtonEnabled(true, ...)` on every applicant.
  - Callers: the `InitiateGUI` DelayedCallback(1), `Xbutton_*`, and `HireRecruit`.
- **CONFIRMED: the Editor's "Astronaut Complex" button opens the same `AstronautComplex` type.**
  - `CrewAssignmentDialog.ButtonAstronautComplex()` -> `onOpenAstronautComplex()` (PreFlightCheck FacilityOperational) -> `private void onOpenACProceed()` -> `GameEvents.onGUIAstronautComplexSpawn.Fire()`.
  - `KSP.UI.Screens.ACSceneSpawner.onACSpawn` handles that event. The same `AstronautComplex` postfixes cover it.

## 8. `KSP.UI.BaseCrewAssignmentDialog` / `KSP.UI.CrewAssignmentDialog`

The namespace is `KSP.UI`, not `KSP.UI.Screens`.

**Signatures**
- `protected virtual void AddAvailItem(ProtoCrewMember crew, UIList list = null, CrewListItem.ButtonTypes type = CrewListItem.ButtonTypes.V)` forwards to `AddAvailItem(crew, out _, list, type)`.
- `protected virtual void AddAvailItem(ProtoCrewMember crew, out CrewListItem item, UIList list = null, CrewListItem.ButtonTypes type = CrewListItem.ButtonTypes.V)`
  - AccessTools types: `{ typeof(ProtoCrewMember), typeof(CrewListItem).MakeByRefType(), typeof(UIList), typeof(CrewListItem.ButtonTypes) }`.
- `private void AddAvailItem(string name, string trait, float xp, int level)` (a third, private overload)
- `protected Sprite disabledCrewListSprite`
- `protected static Color disabledColor = new Color(1f, 1f, 1f, 0.5f)`
- `public UIList scrollListAvail`
- `CrewAssignmentDialog` overrides none of the `AddAvailItem` overloads.

**Claims**
- **CONFIRMED: the stock `crew.inactive` look, inside the `out` overload:**
  ```
  crewListItem.GetComponent<UIDragPanel>().dragEnabled = false;
  UIHoverPanel hp = crewListItem.GetComponent<UIHoverPanel>();
  hp.backgroundImage.sprite = hp.backgroundHover = hp.backgroundNormal = disabledCrewListSprite;
  crewListItem.kerbalName.color = Color.grey; crewListItem.xp_trait.color = Color.grey;
  crewListItem.kerbalSprite.color = disabledColor; crewListItem.MouseoverEnabled = false;
  ```
  - `UIDragPanel.dragEnabled` is a `public bool`.
  - `CreateAvailList` lists only `Kerbals(Crew | Tourist, RosterStatus.Available)`.
- **Note:** the stock inactive branch does not call `SetButtonEnabled`. Adding `SetButtonEnabled(false, "Reserved", why)` for the tooltip is compatible (it only sets `MouseoverEnabled = false` and the tooltip).

## 9. `KSP.UI.Screens.KSCFacilityContextMenu` / `KSP.UI.TooltipTypes.TooltipController_Text`

**Signatures**
- `protected void OnFacilityValuesModified()` (non-virtual, no parameters)
- `protected void OnKSCStructureEvent(DestructibleBuilding)`
- `[SerializeField] private Button UpgradeButton;` and `private TextMeshProUGUI UpgradeButtonText;`
- `private Button DowngradeButton`, `private Button DemolishButton`
- `protected Button RepairButton`, `protected Button EnterButton`
- `public static KSCFacilityContextMenu Create(SpaceCenterBuilding host, Callback<DismissAction>)`
- `protected SpaceCenterBuilding host`

**Claims**
- **CONFIRMED: `onFacilityContextMenuSpawn` fires before the buttons fill.**
  - `Create()` wires the structure events and then does `GameEvents.onFacilityContextMenuSpawn.Fire(component); return component;`.
  - The buttons fill later: `AnchoredDialog.Start()` -> `CreatePanel()` -> `CreateWindowContent()` -> `OnFacilityValuesModified()`.
- **CONFIRMED: `OnFacilityValuesModified` re-runs on structure collapse/repair events,** via `OnKSCStructureEvent`.
  - It sets `UpgradeButton.interactable = true` unless the facility is at max level. Stock does not gate the button on affordability.
- **Upgrade click:** `OnUpgradeButtonInput()` calls `Dismiss(DismissAction.Upgrade)`. Parsek's backstop is `FacilityUpgradeSpendPatch` on `SpaceCenterBuilding.UpgradeFacility(bool)`.
- **CONFIRMED: `TooltipController_Text`.**
  - `public string textString = "No text"` (Localizer-formatted at spawn)
  - `public Tooltip_Text prefab`
  - `RequireInteractable` is a public FIELD on the base `KSP.UI.TooltipController` (`public bool RequireInteractable = true;`), not a property.
- **DIFFERENT: a component added with `AddComponent<TooltipController_Text>()` has `prefab == null`.**
  - `TooltipController.TooltipPrefabType` scans the public `Tooltip`-typed fields, so there is nothing to show.
  - The `prefab` must be copied from an existing stock `TooltipController_Text`. Parsek's `CurrencyReservationOverlay` uses IMGUI, so there is no in-repo precedent.

## 10. `KSP.UI.Screens.Editor.PartListTooltip` and the purchase path

**Signatures** (two overloads confirmed)
- `public void Setup(AvailablePart availablePart, Callback<PartListTooltip> onPurchase, RenderTexture texture = null)`
- `public void Setup(AvailablePart availablePart, PartUpgradeHandler.Upgrade up, Callback<PartListTooltip> onPurchase, RenderTexture texture = null)`
- Also `public void SetupGrayout(string grayoutMessage)`.
- Public fields:
  - `TextMeshProUGUI textGreyoutMessage`
  - `Button buttonPurchase`, `Button buttonPurchaseRed`
  - `GameObject buttonPurchaseContainer`
  - `TextMeshProUGUI buttonPurchaseCaption`, `buttonPurchaseCaptionRed`
- `private void onPurchaseButton()` calls `onPurchase(this)`.
- `protected void onBtnPurchaseRed()` posts the "not enough funds" screen message.

**How stock decides the button**
- `requiresEntryPurchase = !ResearchAndDevelopment.PartModelPurchased(partInfo) && ResearchAndDevelopment.PartTechAvailable(partInfo)`
- `flag = CurrencyModifierQuery.RunQuery(RnDPartPurchase, -entryCost, 0, 0).CanAfford()`
- `buttonPurchase.gameObject.SetActive(flag); buttonPurchaseRed.gameObject.SetActive(!flag);`
- Stock shows one of two buttons; it never sets `interactable`.

**`textGreyoutMessage`**
- `Setup` never writes it. Only `SetupGrayout` does, setting `isGrey = true; textGreyoutMessage.enabled = true; text = ...`.
- `PartListTooltipController.CreateTooltip` calls `SetupGrayout` AFTER `Setup` for greyed icons, so it can overwrite a Setup-postfix message.

**Purchase path** (`KSP.UI.Screens.Editor.PartListTooltipController`)
- `protected void onPurchase(PartListTooltip)` -> PreFlightCheck `FacilityOperational("RnD")` -> `private void onPurchaseProceed()`. It then branches:
  - EDITOR: `techState.partsPurchased.Add(partInfo); GameEvents.OnPartPurchased.Fire(partInfo);` plus identical parts. It does NOT go through `RDTech.PurchasePart`.
  - R&D: `RDController.Instance.node_selected.tech.PurchasePart(partInfo); ...UpdateGraphics(); partList.Refresh(); UpdatePanel();`
- Funds are deducted by `Funding.onPartPurchased` (a `GameEvents.OnPartPurchased` listener): `if (aP.costsFunds) AddFunds(-aP.entryCost, TransactionReasons.RnDPartPurchase)`.
- `RDTech.PurchasePart(AvailablePart)` fires the same event.
- Backstop choice: prefix `PartListTooltipController.onPurchase(PartListTooltip)`, which covers both scenes, plus `RDController.ActionButtonClick("purchase")` for purchase-all. `RDTech.PurchasePart` alone misses the editor.

**KSPCF hooks on `PartListTooltip`:** `UpgradeBugs` postfixes and `PartTooltipUpgradesApplyToSubstituteParts` transpiles + prefixes the 3-argument `Setup`. A Parsek postfix coexists with them.

## 11. Contract Configurator (local source)

- **CONFIRMED: the overrides.** `public override bool CanBeCancelled()` and `public override bool CanBeDeclined()` in `source/ContractConfigurator/ConfiguredContract.cs:381` / `:386`.
  - They return `contractType?.cancellable` / `declinable` (default `true`) and never call base.
  - Class: `ContractConfigurator.ConfiguredContract : Contract`.
- **CONFIRMED: `CanAccept`.** `public static bool CanAccept(Contract contract)` in class `ContractConfigurator.ContractConfigurator` (a MonoBehaviour, namespace `ContractConfigurator`), `ContractConfigurator.cs:570`.
  - It returns false once the per-prestige active count reaches `ContractLimit(prestige)`.
  - Its only caller is `MissionControlUI.cs:1271`, so a postfix is safe.
- **CONFIRMED: `OnSelectContract` overwrites Accept.** In `source/ContractConfigurator/MissionControlUI.cs` (class `ContractConfigurator.Util.MissionControlUI`):
  - `:1267` `MissionControl.Instance.selectedMission = cc.missionSelection;`
  - `:1268` `MissionControl.Instance.UpdateInfoPanelContract(cc.contract);`
  - `:1271` `MissionControl.Instance.btnAccept.interactable = ContractConfigurator.CanAccept(cc.contract) && ContractSystem.Instance.GetActiveContractCount() < maxActive;`
  - CC does not touch `btnDecline` / `btnCancel.interactable`, so the `UpdateInfoPanelContract` postfix for those survives under CC.
- **CC row storage:**
  - `UIListItem.Data` is a `MissionControlUI.ContractContainer` (public nested; fields `public Contract contract`, `public ContractType contractType`, `public MissionControl.MissionSelection missionSelection`).
  - Assignment sites: `mcListItem.container.Data = cc;` at `:781` / `:1034`. Group rows use `GroupContainer` (`:864`).
  - CC builds its rows itself and never calls `MissionControl.AddItem`, so the stock `label` prefix does not reach CC rows.
  - CC's per-row title seam is `protected void SetContractTitle(MCListItem mcListItem, ContractContainer cc)` (`:1398`).
- **CONFIRMED: the Decline/Cancel handlers call stock.**
  - `private void OnClickDecline()` (`:1712-1720`) clears the panel first, then calls `...selectedMission.contract.Decline();` (`:1719`).
  - `OnClickCancel()` calls `.Cancel()` (`:1730`).
  - The handlers are installed with `btnDecline/btnCancel.onClick.RemoveAllListeners()` (`:399-402`), and the same happens for `btnAccept`.
  - Consequences:
    - Parsek's `MissionControlAcceptPatch` (prefix on stock `OnClickAccept`) is bypassed under CC. Only the `Contract.Accept` backstop remains.
    - A refused Decline under CC leaves the info panel cleared while the row stays Offered, which is cosmetic.
  - CC also replaces `GameEvents.Contract.onContractsListChanged` with a new `EventVoid` (`:405`), so stock `RefreshContracts` / `RefreshUIControls` stop firing on list change.

## 12. KSPCF `ShowContractFinishDates`

- **CONFIRMED:** `AddPatch(PatchType.Postfix, typeof(MissionControl), "UpdateInfoPanelContract")`.
- The postfix `(MissionControl __instance, Contract contract)` runs only when `displayMode == Archive`.
- It finds the first `"<b><color=#" + RUIutils.ColorToHex(RichTextUtil.colorParams) + ">"` in `contractText.text`, skips to the next `"\n\n"`, and splices in the accepted and finished dates.
- It does not null-guard `contract`.
- File: `mods/KSPCommunityFixes/KSPCommunityFixes/QoL/ShowContractFinishDates.cs:15-52`.

## 13. Existing Parsek hooks on these types (`Parsek-stock-ui-verify/Source/Parsek`)

| Patch file | Target | Kind |
|---|---|---|
| `Patches/ContractAcceptPatch.cs` `ContractAcceptPatch` | `Contracts.Contract.Accept()` | Prefix (backstop) |
| same file, `MissionControlAcceptPatch` | `MissionControl.OnClickAccept()` (private) | Prefix |
| `Patches/KerbalHirePatch.cs` `KerbalHirePatch` | `KerbalRoster.HireApplicant(ProtoCrewMember)` | Prefix |
| same file, `AstronautComplexHireRecruitPatch` | `AstronautComplex.HireRecruit(UIList, UIList, UIListItem)` | Prefix |
| `Patches/KerbalDismissalPatch.cs` | `KerbalRoster.Remove(ProtoCrewMember)` | Prefix |
| `Patches/CrewDialogFilterPatch.cs` (deleted in PR 6; replaced by `Patches/CrewDialogReservationPatches.cs`: `AddAvailItem` `out` overload and `CreateAvailList` postfixes, `MoveCrewToEmptySeat` / `DropOnCrewList` / `ButtonFill` prefixes) | `BaseCrewAssignmentDialog.AddAvailItem(PCM, UIList, ButtonTypes)` (3-argument, NOT the `out` overload) | Prefix returning false (hides the row) |
| `Patches/CrewAutoAssignPatch.cs` | `BaseCrewAssignmentDialog.RefreshCrewLists(VesselCrewManifest, bool, bool, Func<PartCrewManifest,bool>)` | Prefix (stand-in swap) |
| `Patches/StrategyLifecyclePatch.cs` | `Strategy.Activate()` / `Strategy.Deactivate()` | Postfix (ledger capture, filters `__result`) |
| `Patches/TechResearchPatch.cs` | `RDTech.UnlockTech` | Prefix |
| `Patches/TechResearchSpendPatch.cs` | `RDTech.ResearchTech` | Prefix |
| `Patches/FacilityUpgradeSpendPatch.cs` | `SpaceCenterBuilding.UpgradeFacility(bool)` | Prefix |
| `Patches/FacilityUpgradePatch.cs` | `UpgradeableFacility.SetLevel` | Prefix |
| `Patches/FacilityRepairCapturePatches.cs` | `SpaceCenterBuilding.RepairFacility(bool)`, `ResetStructures` | Prefix/Postfix |
| `StockUiOverlayController.cs` (no Harmony) | subscribes to `RDController.OnRDTreeSpawn/Despawn`, `onGUIAstronautComplexSpawn/Despawn`, `onGUIMissionControlSpawn/Despawn`, `LedgerOrchestrator.OnTimelineDataChanged`; reflects `RDController.nodes` and the four AC `scrollList*` fields; reads `MCListItem.container.Data` as `MissionSelection`/`Contract` (`ExtractMissionControlRowContract`, `:955`) | decorate-after-spawn badges |

- No Parsek patch touches `RDNode`, `RDController`, `Administration`, `KSCFacilityContextMenu`, `PartListTooltip`, `CrewListItem`, `TooltipController_CrewAC`, `Contract.CanBe*`, `Decline` / `Cancel`, or `MissionControl.AddItem` / `UpdateInfoPanelContract`.
- Parsek listens to `GameEvents.OnPartPurchased` for capture only (`GameStateRecorder.cs:316`).

**"One-shot Warn on patch target resolution failure":** there is NO shared helper. The convention is:
- Each patch class has an inline `static MethodBase TargetMethod()` that resolves with `AccessTools.Method` / `GetMethod` and, on null, calls `ParsekLog.Warn(<tag>, "<Type.Method> not found - <what will not apply>...")` before returning null.
- `ParsekHarmony.Awake` (`ParsekHarmony.cs:57-73`) wraps `harmony.CreateClassProcessor(patchType).Patch()` per class in try/catch. It logs `Error "Failed to apply patch X"`, counts `failed`, and on success logs `Verbose "Harmony patch applied: X targets=N"`.
- Runtime reflection failures use per-site `static bool ...Warned` flags:
  - `StockUiOverlayController.rdNodesWarned` / `astronautListsWarned` / `missionRowsWarned`
  - `KspStatePatcher.EmitProtoTechNodesReflectionWarnOnce(string)` (`GameActions/KspStatePatcher.cs:1024`)
  - `LedgerOrchestrator.LogReconcileWarnOnce(key, msg)` (`GameActions/LedgerOrchestrator.cs:1072`)

---

## Surprises (things that change the plan)

1. **Many stock contract types override `CanBeDeclined` / `CanBeCancelled` without calling base.** Nine FinePrint types, both Sentinel types, and the Orbital/Rover construction and VesselRepair templates do it. `ExplorationContract` returns false for both. A postfix on `Contract.CanBeDeclined/CanBeCancelled` plus CC's override misses most stock contracts. Drive `btnDecline` / `btnCancel.interactable` from a `MissionControl.UpdateInfoPanelContract` postfix instead: it is the only stock reader, and CC's select path calls it too. Section 7.4 "Button state" needs rewriting.
2. **Strategy auto-expiry is dead code in pure stock.** `LongestDuration` / `LeastDuration` read never-assigned fields (and `LongestDuration` reads the *Least* fields), so both are 0. The expiry path and the duration gate on `CanBeDeactivated` exist only because KSPCF `StrategyDuration` is installed; it is installed in both the dev and harness instances, so the hazard is real there. The "auto-expiry still deactivates" test cell must run with KSPCF or it proves nothing. The player-path hooks (`Administration.SetSelectedStrategy` postfix, `BtnInputAccept("cancel")` / `OnCancelConfirm` prefix) avoid the issue.
3. **`MissionControl.RefreshUIControls` is not a per-contract seam.** It sets `btnAccept.interactable` globally from the slot count, and it does not run on row selection. The per-contract Accept block goes in the `UpdateInfoPanelContract` postfix, and the `RefreshUIControls` postfix only re-asserts it for `selectedMission`.
4. **CC rows never call `MissionControl.AddItem`.** The `label` prefix covers stock only. CC's title seam is `ContractConfigurator.Util.MissionControlUI.SetContractTitle(MCListItem, ContractContainer)` (protected), or a row scan where `Data` is a `ContractContainer` with a public `contract` field.
5. **A non-empty `AddItem` label replaces the entire row title,** including stock's `#fefa87` colouring and the Archive status prefixes. The prefix must compose the full title.
6. **R&D `actionButton` doubles as "purchase all parts" on researched nodes.** The `UpdatePanel` postfix must be state-aware.
7. **An Astronaut Complex tooltip append must hook `TooltipController_CrewAC.SetTooltip(PCM, string, string)`, not `CrewListItem.SetTooltip`.** `SetButtonEnabled` and `UpdateCrewCounts` rebuild `descriptionString` through the controller directly. Also set `showTooltip = true`. The `AddItem_*` methods return void.
8. **The editor part purchase bypasses `RDTech.PurchasePart`.** It writes `techState.partsPurchased` and fires `OnPartPurchased`. The backstop is `PartListTooltipController.onPurchase` (protected). Stock toggles between `buttonPurchase` and `buttonPurchaseRed` rather than setting `interactable`, and `SetupGrayout` can overwrite a Setup-postfix `textGreyoutMessage`.
9. **An added `TooltipController_Text` needs its `prefab` copied from an existing stock controller.** `RequireInteractable` is a base-class public field.
10. **`Strategy.Load(ConfigNode)` activates without charging** (`isActive = true; Register();`). It is the charge-free ledger-to-stock activation path. Deactivation needs `Unregister()` plus reflection on `isActive`.
11. **`ContractSystem.RebuildContracts()` calls `Cancel()` on every Active contract.** A `Cancel` prefix refuses there too.
12. **CC removes stock's Accept/Decline/Cancel listeners,** so the existing `MissionControlAcceptPatch` (stock `OnClickAccept`) never runs under CC. CC also replaces `onContractsListChanged`.

﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ResourceMonitor.Components
{
    /**
     * Stores information about a resource. The type, amount and what storage containers contain this.
     */
    public class TrackedResource
    {
        public TechType TechType { get; set; }
        public int Amount { get; set; }
        public HashSet<StorageContainer> Containers { get; set; }
    }

    /**
    * Component that contains all the logic related to the resource monitor component.
    * When set up: We find the base we was placed in. Get all StorageContainer components in that base gameobject children.
    * We then subscribe to the events of items getting added and removed from that StorageContainer.
    * We care about when a new locker is added though as we need to subscribe to its events we use Harmony's Postfix on StorageContainer.Awake() for that.
    * We care about when a locker is removed though so we can unsubscribe from it. Either if its getting deleted or unloaded due to game exit. TO:DO implemenet this.
    */
    public class ResourceMonitorLogic : MonoBehaviour, IConstructable
    {
        private static readonly float COOLDOWN_TIME_BETWEEN_PICKING_UP_LAST_ITEM_TYPE = 1f;

        public SortedDictionary<TechType, TrackedResource> TrackedResources { private set; get; } = new SortedDictionary<TechType, TrackedResource>();
        private ResourceMonitorDisplay rmd;
        private GameObject seaBase;
        private bool isEnabled = false;
        private float timerTillNextPickup = .0f;

        private IEnumerator Startup()
        {
            yield return new WaitForEndOfFrame();

            seaBase = gameObject?.transform?.parent?.gameObject;
            if (seaBase == null)
            {
                ErrorMessage.AddMessage("[ResourceMonitor] ERROR: Can not work out what base it was placed inside.");
                System.Console.WriteLine("[ResourceMonitor] ERROR: Can not work out what base it was placed inside.");
                yield break;
            }

            TrackExistingStorageContainers();
            Patchers.StorageContainerAwakePatcher.AddEventHandlerIfMissing(AlertedNewStorageContainerPlaced);
            TurnDisplayOn();
        }

        public void OnDestroy()
        {
            Patchers.StorageContainerAwakePatcher.RemoveEventHandler(AlertedNewStorageContainerPlaced);
            TrackedResources.Clear();
        }

        public void OnConstructedChanged(bool constructed)
        {
            if (constructed)
            {
                if (isEnabled == false)
                {
                    isEnabled = true;
                    StartCoroutine(Startup());
                }
                else
                {
                    TurnDisplayOn();
                }
            }
            else
            {
                if (isEnabled)
                {
                    TurnDisplayOff();
                }
            }
        }

        public bool CanDeconstruct(out string reason)
        {
            reason = null;
            return true;
        }

        private void TurnDisplayOn()
        {
            if (rmd != null)
            {
                TurnDisplayOff();
            }

            rmd = gameObject.AddComponent<ResourceMonitorDisplay>();
            rmd.Setup(this);
        }

        private void TurnDisplayOff()
        {
            if (rmd != null)
            {
                rmd.TurnDisplayOff();
                Destroy(rmd);
                rmd = null;
            }
        }

        private void TrackExistingStorageContainers()
        {
            StorageContainer[] containers = seaBase.GetComponentsInChildren<StorageContainer>();
            foreach (StorageContainer sc in containers)
            {
                TrackStorageContainer(sc);
            }
        }

        private void AlertedNewStorageContainerPlaced(StorageContainer sc)
        {
            StartCoroutine("TrackNewStorageContainerCoroutine", sc);
        }

        public IEnumerator TrackNewStorageContainerCoroutine(StorageContainer sc)
        {
            // We yield to the end of the frame as we need the parent/children tree to update.
            yield return new WaitForEndOfFrame();
            GameObject newSeaBase = sc?.gameObject?.transform?.parent?.gameObject;
            if (newSeaBase != null && newSeaBase == seaBase)
            {
                TrackStorageContainer(sc);
            }

            StopCoroutine("TrackNewStorageContainerCoroutine");
        }

        private void TrackStorageContainer(StorageContainer sc)
        {
            if (sc == null || sc.container == null)
            {
                return;
            }

            foreach (var item in sc.container.GetItemTypes())
            {
                AddItemsToTracker(sc, item, sc.container.GetCount(item));
            }

            sc.container.onAddItem += (item) => AddItemsToTracker(sc, item.item.GetTechType());
            sc.container.onRemoveItem += (item) => RemoveItemsFromTracker(sc, item.item.GetTechType());
        }

        private void AddItemsToTracker(StorageContainer sc, TechType item, int amountToAdd = 1)
        {
            if (TrackedResources.ContainsKey(item))
            {
                TrackedResources[item].Amount = TrackedResources[item].Amount + amountToAdd;
                TrackedResources[item].Containers.Add(sc);
            }
            else
            {
                TrackedResources.Add(item, new TrackedResource()
                {
                    TechType = item,
                    Amount = amountToAdd,
                    Containers = new HashSet<StorageContainer>()
                    {
                        sc
                    }
                });
            }

            rmd?.ItemModified(item, TrackedResources[item].Amount);
        }

        private void RemoveItemsFromTracker(StorageContainer sc, TechType item, int amountToRemove = 1)
        {
            if (TrackedResources.ContainsKey(item))
            {
                TrackedResource trackedResource = TrackedResources[item];
                int newAmount = trackedResource.Amount - amountToRemove;
                trackedResource.Amount = newAmount;

                if (newAmount <= 0)
                {
                    TrackedResources.Remove(item);
                }
                else
                {
                    int amountLeftInContainer = sc.container.GetCount(item);
                    if (amountLeftInContainer <= 0)
                    {
                        trackedResource.Containers.Remove(sc);
                    }
                }

                rmd?.ItemModified(item, newAmount);
            }
        }

        public void Update()
        {
            if (timerTillNextPickup > 0f)
            {
                timerTillNextPickup -= Time.deltaTime;
            }
        }

        public void AttemptToTakeItem(TechType item)
        {
            if (timerTillNextPickup > 0f)
            {
                return;
            }

            if (TrackedResources.ContainsKey(item))
            {
                TrackedResource trackedResource = TrackedResources[item];
                if (trackedResource.Containers.Count >= 1)
                {
                    StorageContainer sc = trackedResource.Containers.ElementAt(0);
                    if (sc.container.Contains(item))
                    {
                        GameObject gameObject = CraftData.InstantiateFromPrefab(item, false);
                        if (gameObject == null)
                        {
                            return;
                        }

                        gameObject.transform.position = MainCamera.camera.transform.position + MainCamera.camera.transform.forward * 3f;
                        CrafterLogic.NotifyCraftEnd(gameObject, item);
                        Pickupable pickup = gameObject.GetComponent<Pickupable>();
                        if (pickup == null)
                        {
                            return;
                        }

                        bool addResult = Inventory.main.Pickup(pickup);
                        if (addResult)
                        {
                            if (trackedResource.Amount == 1)
                            {
                                timerTillNextPickup = COOLDOWN_TIME_BETWEEN_PICKING_UP_LAST_ITEM_TYPE;
                            }
                            sc.container.RemoveItem(item);
                        }
                    }
                }   
            }
        }
    }
}

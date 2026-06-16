//--------------------------------------------------------------------------//
// Copyright 2023-2026 Chocolate Dinosaur Ltd. All rights reserved.         //
// For full documentation visit https://www.chocolatedinosaur.com           //
//--------------------------------------------------------------------------//

using UnityEngine;
using UnityEngine.EventSystems;

namespace ChocDino.UIFX.Demos
{
	/// <summary>
	/// Activate the correct input module based on the installed input system.
	/// </summary>
	class InputModuleActivator : MonoBehaviour
	{
		void Awake()
		{
			#if UNITY_2022_3_OR_NEWER
			var eventSystem = FindAnyObjectByType<EventSystem>();
			#else
			var eventSystem = FindObjectOfType<EventSystem>();
			#endif
			
			GameObject go = null;
			if (eventSystem)
			{
				go = eventSystem.gameObject;
			}
			
			if (go == null)
			{
				go = new GameObject("EventSystem");
			}

			if (go != null)
			{
				if (eventSystem == null)
				{
					go.AddComponent<EventSystem>();
				}
				#if ENABLE_LEGACY_INPUT_MANAGER
				go.AddComponent<StandaloneInputModule>();
				#elif ENABLE_INPUT_SYSTEM && PACKAGE_INPUTSYSTEM
				go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
				#else
				Debug.LogError("[UIFX] No input system found. Demos require input system set in Project Settings.");
				Debug.Break();
				#endif
			}

		}
	}
}
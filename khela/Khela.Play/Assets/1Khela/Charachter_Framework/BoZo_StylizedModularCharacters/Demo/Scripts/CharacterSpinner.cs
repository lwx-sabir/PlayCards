using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Bozo.ModularCharacters
{
    public class CharacterSpinner : MonoBehaviour, IDragHandler, IPointerDownHandler, IPointerUpHandler
    {
        public CharacterCreator CharacterCreator;
        public float spinDir;
        public GameObject character;
        private Animator anim;
        float dizzyTimer = 1;

        bool spinning;

        private void OnEnable()
        {
            if (CharacterCreator) CharacterCreator.onCharacterChanged += SetCharacter;
        }

        private void OnDisable()
        {
            if (CharacterCreator) CharacterCreator.onCharacterChanged -= SetCharacter;
        }

        public void SetCharacter(GameObject character)
        {
            this.character = character;
            anim = character.GetComponentInChildren<Animator>();
        }

        public void OnDrag(PointerEventData eventData)
        {
            spinDir = -eventData.delta.x * 0.1f;
            character.transform.Rotate(0, spinDir, 0);
            dizzyTimer = 0.5f;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            spinning = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            spinning = false;
            OnDrag(eventData);
        }

        private void Start()
        {
            SetCharacter(character);
        }

        private void Update()
        {
            if (dizzyTimer <= 0)
            {
                if (spinDir >= 5 || spinDir <= -5)
                {
                    anim.SetBool("Dizzy", true);
                }
                else
                {
                    anim.SetBool("Dizzy", false);
                }
            }

            if (!spinning)
            {
                character.transform.Rotate(0, spinDir, 0);
                spinDir = Mathf.Lerp(spinDir, 0, Time.deltaTime);
                dizzyTimer -= Time.deltaTime;
            }
        }
    }
}

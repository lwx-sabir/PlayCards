// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

using System.Collections;
using System.Collections.Generic;

namespace Sonity.Internal {

    public static class ArrayUtilities {

        public static void Add<T>(ref T[] inputArray, T item) {
            System.Array.Resize(ref inputArray, inputArray.Length + 1);
            inputArray[inputArray.Length - 1] = item;
        }

        public static void Insert<T>(ref T[] inputArray, int index, T item) {
            ArrayList arrayList = new ArrayList();
            arrayList.AddRange(inputArray);
            arrayList.Insert(index, item);
            inputArray = arrayList.ToArray(typeof(T)) as T[];
        }

        public static void Remove<T>(ref T[] inputArray, T item) {
            List<T> list = new List<T>(inputArray);
            list.Remove(item);
            inputArray = list.ToArray();
        }

        public static void RemoveAt<T>(ref T[] inputArray, int index) {
            List<T> list = new List<T>(inputArray);
            list.RemoveAt(index);
            inputArray = list.ToArray();
        }

        public static bool Contains<T>(T[] inputArray, T item) {
            List<T> list = new List<T>(inputArray);
            return list.Contains(item);
        }

        public static void Clear<T>(ref T[] inputArray) {
            System.Array.Clear(inputArray, 0, inputArray.Length);
            System.Array.Resize(ref inputArray, 0);
        }
    }
}


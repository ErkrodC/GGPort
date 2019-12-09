﻿using System;
using System.Collections;
using System.Collections.Generic;

namespace GGPort {
	public class CircularQueue<T> : IEnumerable<T> {
		public int Count { get; private set; }
		
		private readonly int capacity;
		private readonly T[] elements;
		private int head;
		private int tail;

		public CircularQueue(int capacity) {
			this.capacity = capacity;
			elements = new T[capacity];
			head = 0;
			tail = 0;
			Count = 0;
		}

		public T this[int i] {
			get {
				if (i >= Count) { throw new IndexOutOfRangeException(); }

				return elements[(tail + i) % capacity];
			}
		}

		public ref T Peek() {
			if (Count <= 0 || Count > capacity) { throw new IndexOutOfRangeException(); }

			return ref elements[tail];
		}

		public T Pop() {
			if (Count <= 0 || Count > capacity) { throw new IndexOutOfRangeException(); }

			T value = elements[tail];

			tail = (tail + 1) % capacity;
			--Count;

			return value;
		}

		public void Push(T value) {
			if (Count >= capacity) { throw new IndexOutOfRangeException(); }

			elements[head] = value;
			head = (head + 1) % capacity;
			++Count;
		}

		public IEnumerator<T> GetEnumerator() {
			return new CircleQueueEnumerator(this);
		}

		IEnumerator IEnumerable.GetEnumerator() {
			return GetEnumerator();
		}

		private struct CircleQueueEnumerator : IEnumerator<T> {
			private readonly CircularQueue<T> queue;
			private int position;
			
			public CircleQueueEnumerator(CircularQueue<T> queue) {
				this.queue = queue;
				position = -1;
			}

			public bool MoveNext() {
				position++;
				return position < queue.Count;
			}

			public void Reset() {
				position = -1;
			}

			public T Current {
				get {
					try {
						return queue[position];
					} catch (IndexOutOfRangeException) {
						throw new InvalidOperationException();
					}
				}
			}

			object IEnumerator.Current => Current;

			public void Dispose() { }
		}
	}
}
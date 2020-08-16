using System;
using System.Collections;
using System.Collections.Generic;

namespace GGPort {
	public class CircularQueue<T> : IEnumerable<T> {
		public int count { get; private set; }

		private readonly int _capacity;
		private readonly T[] _elements;
		private int _head;
		private int _tail;

		public CircularQueue(int capacity) {
			_capacity = capacity;
			_elements = new T[capacity];
			_head = 0;
			_tail = 0;
			count = 0;
		}

		private T this[int i] {
			get {
				Platform.Assert(i < count);

				return _elements[(_tail + i) % _capacity];
			}
		}

		public ref T Peek() {
			Platform.Assert(0 < count && count <= _capacity);

			return ref _elements[_tail];
		}

		public T Pop() {
			Platform.Assert(0 < count && count <= _capacity);

			T value = _elements[_tail];

			_tail = (_tail + 1) % _capacity;
			--count;

			return value;
		}

		public void Push(T value) {
			Platform.Assert(count < _capacity);

			_elements[_head] = value;
			_head = (_head + 1) % _capacity;
			++count;
		}

		public IEnumerator<T> GetEnumerator() {
			return new CircleQueueEnumerator(this);
		}

		IEnumerator IEnumerable.GetEnumerator() {
			return GetEnumerator();
		}

		private struct CircleQueueEnumerator : IEnumerator<T> {
			private readonly CircularQueue<T> _queue;
			private int _position;

			public CircleQueueEnumerator(CircularQueue<T> queue) {
				_queue = queue;
				_position = -1;
			}

			public bool MoveNext() {
				_position++;
				return _position < _queue.count;
			}

			public void Reset() {
				_position = -1;
			}

			public T Current {
				get {
					try {
						return _queue[_position];
					} catch (Exception exception) {
						Platform.AssertFailed(exception);
						throw;
					}
				}
			}

			object IEnumerator.Current => Current;

			public void Dispose() { }
		}
	}
}
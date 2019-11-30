using System;

namespace GGPort {
	public class RingBuffer<T> {
		protected T[] _elements;
		protected int _head;
		protected int _tail;
		protected int _size;
		protected int _capacity;
		
		public RingBuffer(int capacity) {
			_capacity = capacity;
			_elements = new T[_capacity];
			_head = 0;
			_tail = 0;
			_size = 0;
		}

		public ref T front() {
			if (_size == _capacity) { throw new ArgumentOutOfRangeException(); }

			return ref _elements[_tail];
		}

		public T item(int i) {
			if (i >= _size) { throw new ArgumentOutOfRangeException(); }

			return _elements[(_tail + i) % _capacity];
		}

		public void pop() {
			if (_size == _capacity) { throw new ArgumentOutOfRangeException(); }

			_tail = (_tail + 1) % _capacity;
			_size--;
		}

		public void push(T t) {
			if (_size == _capacity - 1) { throw new ArgumentOutOfRangeException(); }

			_elements[_head] = t;
			_head = (_head + 1) % _capacity;
			_size++;
		}

		public int size() {
			return _size;
		}

		public bool empty() {
			return _size == 0;
		}
	}
}
/* -----------------------------------------------------------------------
* GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
*
* Use of this software is governed by the MIT license that can be found
* in the LICENSE file.
*/

using System;

namespace GGPort {
	// TODO wholly unnecessary in C#
	public class StaticBuffer<T> where T : class {
		public StaticBuffer(int capacity) {
			_size = 0;
			_capacity = capacity;
			_elements = new T[_capacity];
		} 

		public T this[int i] {
			get {
				if (i < 0 || i >= _size) {
					throw new IndexOutOfRangeException();
				}
				
				return _elements[i];
			}

			set {
				if (i < 0 || i >= _size) {
					throw new IndexOutOfRangeException();
				}

				_elements[i] = value;
			}
		}

		public void push_back(T t) {
			if (_size == _capacity - 1) {
				throw new OverflowException();
			}
			
			_elements[_size++] = t;
		}

		public int size() {
			return _size;
		}
		
		protected T[] _elements;
		protected int _size;
		protected int _capacity;
	}
}

/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using IEnumerable = System.Collections.IEnumerable;

namespace OpenSim.Region.OptionalModules.Scripting.Minimodule
{
    public class ObjectAccessor : System.MarshalByRefObject, IObjectAccessor
    {
        private readonly Scene m_scene;
        private readonly ISecurityCredential m_security;

        public ObjectAccessor(Scene scene, ISecurityCredential security)
        {
            m_scene = scene;
            m_security = security;
        }

        public int Count
        {
            get { return m_scene.Entities.Count; }
        }

        public bool IsReadOnly
        {
            get { return true; }
        }

        public IObject this[int index]
        {
            get
            {
                return new SOPObject(m_scene, m_scene.Entities[(uint)index].LocalId, m_security);
            }
        }

        public IObject this[uint index]
        {
            get
            {
                return new SOPObject(m_scene, m_scene.Entities[index].LocalId, m_security);
            }
        }

        public IObject this[UUID index]
        {
            get
            {
                return new SOPObject(m_scene, m_scene.Entities[index].LocalId, m_security);
            }
        }

        public void Add(IObject item)
        {
            throw new NotSupportedException("Collection is read-only. This is an API TODO FIX, creation of objects is presently impossible.");
        }

        public void Clear()
        {
            throw new NotSupportedException("Collection is read-only. TODO FIX.");
        }

        public bool Contains(IObject item)
        {
            return m_scene.Entities.ContainsKey(item.LocalID);
        }

        public void CopyTo(IObject[] array, int arrayIndex)
        {
            for (int i = arrayIndex; i < Count + arrayIndex; i++)
            {
                array[i] = this[i - arrayIndex];
            }
        }

        public IObject Create(Vector3 position)
        {
            return Create(position, Quaternion.Identity);
        }

        public IObject Create(Vector3 position, Quaternion rotation)
        {
            SceneObjectGroup sog = m_scene.AddNewPrim(m_security.owner.GlobalID,
                                                      UUID.Zero,
                                                      position,
                                                      rotation,
                                                      PrimitiveBaseShape.CreateBox());

            IObject ret = new SOPObject(m_scene, sog.LocalId, m_security);

            return ret;
        }

        public IEnumerator<IObject> GetEnumerator()
        {
            return new IObjEnum(m_scene, m_security);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool Remove(IObject item)
        {
            throw new NotSupportedException("Collection is read-only. TODO FIX.");
        }
    }

    internal class IObjEnum : System.MarshalByRefObject, IEnumerator<IObject>
    {
        private readonly List<EntityBase> m_entities;
        private readonly Scene m_scene;
        private readonly ISecurityCredential m_security;
        private readonly IEnumerator<EntityBase> m_sogEnum;

        public IObjEnum(Scene scene, ISecurityCredential security)
        {
            m_scene = scene;
            m_security = security;
            m_entities = new List<EntityBase>(m_scene.Entities.GetEntities());
            m_sogEnum = m_entities.GetEnumerator();
        }

        public IObject Current
        {
            get
            {
                return new SOPObject(m_scene, m_sogEnum.Current.LocalId, m_security);
            }
        }

        object IEnumerator.Current
        {
            get { return Current; }
        }

        public void Dispose()
        {
            m_sogEnum.Dispose();
        }

        public bool MoveNext()
        {
            return m_sogEnum.MoveNext();
        }

        public void Reset()
        {
            m_sogEnum.Reset();
        }
    }
}
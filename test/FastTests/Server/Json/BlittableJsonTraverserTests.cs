﻿using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Xunit;

namespace FastTests.Server.Json
{
    public class BlittableJsonTraverserTests : RavenLowLevelTestBase
    {
        private readonly JsonOperationContext _ctx;
        private readonly List<BlittableJsonReaderObject> _docs = new List<BlittableJsonReaderObject>();
        private readonly BlittableJsonTraverser _sut = new BlittableJsonTraverser();

        public BlittableJsonTraverserTests()
        {
            _ctx = new JsonOperationContext();
        }

        [Fact]
        public void Reads_simple_value()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Name"] = "John Doe"
            });

            object read;
            _sut.TryRead(doc, "Name", out read);

            Assert.Equal("John Doe", read.ToString());
        }

        [Fact]
        public void Reads_nested_values()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Name"] = "John Doe",
                ["Address"] = new DynamicJsonValue
                {
                    ["City"] = "New York City",
                    ["Details"] = new DynamicJsonValue
                    {
                        ["Floor"] = 2
                    }
                }
            });

            object read;
            _sut.TryRead(doc, "Address.City", out read);

            Assert.Equal("New York City", read.ToString());

            _sut.TryRead(doc, "Address.Details.Floor", out read);

            Assert.Equal(2L, read);
        }

        [Fact]
        public void Reads_values_nested_in_array()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Friends"] = new DynamicJsonArray
                {
                    new DynamicJsonValue
                    {
                        ["Name"] = "Joe"
                    },
                    new DynamicJsonValue
                    {
                        ["Name"] = "John"
                    }
                }
            });

            object read;
            _sut.TryRead(doc, "Friends,Name", out read);

            var enumerable = read as IEnumerable<object>;

            Assert.NotNull(enumerable);

            var items = enumerable.ToList();

            Assert.Equal(2, items.Count);
            Assert.Equal("Joe", items[0].ToString());
            Assert.Equal("John", items[1].ToString());
        }

        [Fact]
        public void Reads_values_nested_in_array_as_objects()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Friends"] = new DynamicJsonArray
                {
                    new DynamicJsonValue
                    {
                        ["Name"] = new DynamicJsonValue
                        {
                            ["First"] = "Joe"
                        }
                    },
                    new DynamicJsonValue
                    {
                        ["Name"] = new DynamicJsonValue
                        {
                            ["First"] = "John"
                        }
                    },
                }
            });

            object read;
            _sut.TryRead(doc, "Friends,Name.First", out read);

            var enumerable = read as IEnumerable<object>;

            Assert.NotNull(enumerable);

            var items = enumerable.ToList();

            Assert.Equal(2, items.Count);
            Assert.Equal("Joe", items[0].ToString());
            Assert.Equal("John", items[1].ToString());
        }

        [Fact]
        public void Reads_value_nested_in_object_of_array_of_arrays()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Name"] = "John Doe",
                ["Items"] = new DynamicJsonArray
                {
                    new DynamicJsonArray
                    {
                        new DynamicJsonValue
                        {
                            ["Bar"] = new DynamicJsonValue
                            {
                                ["Foo"] = "foo/1"
                            }
                        },
                        new DynamicJsonValue
                        {
                            ["Bar"] = new DynamicJsonValue
                            {
                                ["Foo"] = "foo/2"
                            }
                        },
                        new DynamicJsonValue
                        {
                            ["Bar"] = new DynamicJsonValue
                            {
                                ["Foo"] = "foo/3"
                            }
                        }
                    }
                }
            });

            object read;
            _sut.TryRead(doc, "Items,,Bar.Foo", out read);

            var enumerable = read as IEnumerable<object>;

            Assert.NotNull(enumerable);

            var items = enumerable.ToList();

            Assert.Equal(3, items.Count);
            Assert.Equal("foo/1", items[0].ToString());
            Assert.Equal("foo/2", items[1].ToString());
            Assert.Equal("foo/3", items[2].ToString());
        }

        [Fact]
        public void Reads_value_nested_in_nested_object_of_array_of_arrays()
        {
            var doc = create_doc(new DynamicJsonValue
            {
                ["Name"] = "John Doe",
                ["Items"] = new DynamicJsonArray
                {
                    new DynamicJsonValue
                    {
                        ["Bar"] = new DynamicJsonArray
                        {
                            new DynamicJsonValue
                            {
                                ["Foo"] = new DynamicJsonValue
                                {
                                    ["Baz"] = "baz/1"
                                }
                            },
                            new DynamicJsonValue
                            {
                                ["Foo"] = new DynamicJsonValue
                                {
                                    ["Baz"] = "baz/2"
                                }
                            }
                        }
                    },
                    new DynamicJsonValue
                    {
                        ["Bar"] = new DynamicJsonArray
                        {
                            new DynamicJsonValue
                            {
                                ["Foo"] = new DynamicJsonValue
                                {
                                    ["Baz"] = "baz/3"
                                }
                            }
                        }
                    },
                }
            });

            object read;
            _sut.TryRead(doc, "Items,Bar,Foo.Baz", out read);

            var enumerable = read as IEnumerable<object>;

            Assert.NotNull(enumerable);

            var items = enumerable.ToList();

            Assert.Equal(3, items.Count);
            Assert.Equal("baz/1", items[0].ToString());
            Assert.Equal("baz/2", items[1].ToString());
            Assert.Equal("baz/3", items[2].ToString());
        }

        public BlittableJsonReaderObject create_doc(DynamicJsonValue document)
        {
            var doc = _ctx.ReadObject(document, string.Empty);

            _docs.Add(doc);

            return doc;
        }

        public override void Dispose()
        {
            foreach (var docReader in _docs)
            {
                docReader.Dispose();
            }

            _ctx.Dispose();

            base.Dispose();
        }
    }
}
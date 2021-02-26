using System;
using System.Collections.Generic;
using System.Linq;
using Echo.Core.Graphing;
using Echo.DataFlow.Collections;

namespace Echo.DataFlow
{
    /// <summary>
    /// Represents a single node in a data flow graph.
    /// </summary>
    /// <typeparam name="TContents">The type of contents to store in the node.</typeparam>
    public class DataFlowNode<TContents> : IIdentifiedNode
    {
        /// <summary>
        /// Creates a new data flow graph node.
        /// </summary>
        /// <param name="id">A unique identifier for the node that can be used for indexing the node.</param>
        /// <param name="contents">The contents of the node.</param>
        public DataFlowNode(long id, TContents contents)
        {
            Id = id;
            Contents = contents;
            StackDependencies = new StackDependencyCollection<TContents>(this);
            VariableDependencies = new VariableDependencyCollection<TContents>(this);
        }

        /// <summary>
        /// Gets the data flow graph this node is a part of.
        /// </summary>
        public DataFlowGraph<TContents> ParentGraph
        {
            get;
            internal set;
        }

        /// <inheritdoc />
        public long Id
        {
            get;
            internal set;
        }

        /// <inheritdoc />
        public int InDegree => Dependants.Count;

        /// <inheritdoc />
        public int OutDegree => StackDependencies.EdgeCount + VariableDependencies.EdgeCount;

        /// <summary>
        /// Gets a value indicating whether the data flow node represents an external data source.
        /// </summary>
        public virtual bool IsExternal => false;

        /// <summary>
        /// Gets the contents of the node.
        /// </summary>
        public TContents Contents
        {
            get;
            set;
        }

        /// <summary>
        /// Gets a collection of values allocated on a stack that this node depends on.
        /// </summary>
        public StackDependencyCollection<TContents> StackDependencies
        {
            get;
        }

        /// <summary>
        /// Gets a collection of values that are assigned to variables that this node depends on.
        /// </summary>
        public VariableDependencyCollection<TContents> VariableDependencies
        {
            get;
        }

        internal IList<DataFlowNode<TContents>> Dependants
        {
            get;
        } = new List<DataFlowNode<TContents>>();

        /// <summary>
        /// Obtains a collection of nodes that depend on this node.
        /// </summary>
        /// <returns>The dependant nodes.</returns>
        public IEnumerable<DataFlowNode<TContents>> GetDependants() => Dependants;

        IEnumerable<IEdge> INode.GetIncomingEdges()
        {
            foreach (var dependant in Dependants)
            {
                //if this is a stack dep 
                if (dependant.StackDependencies.Any(x => x.Select(x => x.Node).Contains(this)))
                {
                    var index = dependant.StackDependencies.IndexOf(
                        dependant.StackDependencies.First(x => x.Select(y => y.Node).Contains(this)));
                    yield return new DataFlowEdge<TContents>(dependant, this, DataDependencyType.Stack, index);
                }
                //if this is a variable dep
                else
                {
                    var dependency =
                        dependant.VariableDependencies.First(x => x.Value.Select(y => y.Node).Contains(this));
                    yield return new DataFlowEdge<TContents>(dependant, this, DataDependencyType.Variable, dependency.Key);
                }
            }
        }

        IEnumerable<IEdge> INode.GetOutgoingEdges()
        {
            for (int i = 0; i < StackDependencies.Count; i++)
            {
                foreach (var source in StackDependencies[i])
                    yield return new DataFlowEdge<TContents>(this, source.Node, DataDependencyType.Stack, i);
            }

            foreach (var dependency in VariableDependencies)
            {
                foreach (var source in dependency.Value)
                    yield return new DataFlowEdge<TContents>(this, source.Node, DataDependencyType.Variable, dependency.Key);
            }
        }

        IEnumerable<INode> INode.GetPredecessors() => Dependants;

        IEnumerable<INode> INode.GetSuccessors() => StackDependencies
            .SelectMany(
                dep => dep,
                (dep, source) => source.Node);

        bool INode.HasPredecessor(INode node) => Dependants.Contains(node);

        bool INode.HasSuccessor(INode node) => StackDependencies.Any(
            dep => dep.Any(source => source.Node == node));

        /// <summary>
        /// Removes all incident edges (both incoming and outgoing edges) from the node, effectively isolating the node
        /// in the graph. 
        /// </summary>
        public void Disconnect()
        {
            foreach (var dependency in StackDependencies)
                dependency.Clear();
            foreach (var entry in VariableDependencies)
                entry.Value.Clear();

            foreach (var dependant in Dependants.ToArray())
            {
                foreach (var dependency in dependant.StackDependencies)
                    dependency.Remove(this);

                foreach (var entry in dependant.VariableDependencies)
                    entry.Value.Remove(this);
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"{Id} ({Contents})";
    }
}